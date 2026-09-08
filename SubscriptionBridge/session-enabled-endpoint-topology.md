# Session-enabled endpoints with ordered subscriptions

## Short version

Ordered subscriptions should not be consumed directly into handlers. When sessions are enabled, the endpoint input queue is the ordered processing boundary. Ordered subscriptions are consumed by the transport only as a bridge into that queue.

```text
publisher
  -> topic
    -> session-enabled subscription, no ForwardTo
      -> transport-owned subscription bridge
        -> session-enabled endpoint input queue
          -> endpoint session pump
            -> handlers
```

The important part is that the handler pipeline still sees one endpoint input queue. The subscription receivers are an implementation detail of the transport.

This keeps subscriptions as transport infrastructure rather than making them first-class application receive endpoints.

## Why I think the current direction feels off

Azure Service Bus does not allow auto-forwarding from a session-enabled subscription. That part is not really up for debate.

The broker constraint does not require consuming subscriptions directly into the endpoint pipeline or forcing users into many small endpoints for every ordered event group. A transport-owned bridge keeps that infrastructure limitation out of the application programming model.

Today our topology is built around this model:

```text
topic subscription -> ForwardTo -> endpoint input queue -> endpoint pump
```

The code reflects that. We create subscriptions with `ForwardTo = inputQueue` in:

- `src/Transport/EventRouting/TopicPerEventTopologySubscriptionManager.cs`
- `src/Transport/EventRouting/MigrationTopologySubscriptionManager.cs`

That gives us a nice property: the endpoint has one input queue that represents its work. Recoverability, ServiceControl retry, monitoring, and operational visibility all line up around that queue.

That property remains useful for recoverability, monitoring, and operational visibility.

## Proposal

Introduce a transport-level sessions mode.

When enabled:

1. The endpoint input queue is created with `RequiresSession = true`.
2. The endpoint input queue is consumed with a session processor.
3. Ordered subscriptions are created with `RequiresSession = true` and without `ForwardTo`.
4. The transport starts internal session processors for those ordered subscriptions.
5. Those processors copy messages into the endpoint input queue and preserve the native `SessionId`.
6. Handlers are invoked only from the endpoint input queue.
7. Session-aware recoverability is implemented once, in the input queue session pump.

The subscription bridge should be boring on purpose. It should not know about handlers, recoverability policy, delayed retries, or session blocking. It should only do this:

```text
receive from subscription
copy body, headers, and relevant native properties
preserve SessionId
send to endpoint input queue
complete the subscription message after the send succeeds
```

The input queue session pump is where we put the recoverability and blocking logic. Scheduled delayed retries and terminal DLQ bring-backs use the same durable hold-back.

## Why this seems better

### We keep the endpoint model intact

Users still have an endpoint input queue as the common processing boundary.

If a message fails, gets moved to the error queue, is picked up by ServiceControl, and is later retried, it naturally comes back to the endpoint input queue. That is the place where we can inspect the session state and decide whether this message unblocks the session.

Direct subscription consumption would distribute recoverability, blocking, retry, concurrency, and shutdown behavior across several receive points.

### We relocate back-pressure to the right place

Without `ForwardTo`, messages remain in the subscription until the bridge drains them, and subscriptions count against the **topic's** size quota. While the input queue accepts sends, the bridge moves normal buffering to the **endpoint queue's** quota. During destination failure, the processor can hold a bounded active source delivery; repeated send failures and redeliveries can consume delivery attempts and eventually dead-letter the source message. No deliberate production escape or critical-error policy exists for that condition yet. The bridge relocates buffering pressure while the destination has capacity; it does not remove back-pressure.

### We solve session blocking once

We need explicit session blocking anyway.

If message `A1` in session `Customer-123` fails and goes to the error queue, we cannot just continue processing `A2`, `A3`, and `A4` from the same session and still claim ordered processing. Something has to remember that `Customer-123` is blocked until `A1` is retried or discarded.

Doing that at the input queue is much easier to reason about:

```text
session Customer-123 is blocked
session Customer-456 is not blocked

skip/release Customer-123
continue with Customer-456
```

Keeping that decision in one input queue pump avoids repeating it across direct subscription pumps.

### It gives commands and events the same input boundary

A session-enabled endpoint has one ordered input boundary.

That boundary can receive:

- commands sent directly to the endpoint
- events bridged from ordered subscriptions
- ServiceControl retries

All of those go through the same session pump and the same recoverability behavior.

### It does not invent global ordering

This design should not try to provide ordering across independent Azure Service Bus entities. That is not a guarantee Service Bus sessions provide either.

A session-enabled queue or subscription owns its own ordered stream. If an application consumes from two topics through two session-enabled subscriptions, there is no broker-level coordination between those subscriptions, even if messages on both subscriptions use the same `SessionId`.

The bridge does not change that. It moves ordered source streams into the endpoint input queue. From that point on, the input queue establishes the order for each `SessionId`, and the endpoint session pump preserves that order while applying one recoverability model.

So the guarantee is:

> We preserve ordered endpoint processing per session at the input queue boundary. For bridged subscriptions, we preserve the source subscription's per-session order into that boundary where possible. We do not claim a global order across independent topics, subscriptions, commands, or retries because Azure Service Bus does not provide such a global order either.

This is the meaningful guarantee available without inventing a distributed ordering coordinator on top of Service Bus.

## The ordering model


A session gives us two things, and only two: happy-path FIFO (when nothing fails, `A1` is delivered and completed before `A2` is handed over), and an exclusive lock with colocated state (one receiver owns the session, and there is a place to stash metadata).

It does **not** give us ordering across a failure. The moment `A1` fails and we want anything other than instant abandon-and-reserve, ordering is gone from the broker's point of view and becomes our problem. This is not a guess — the Azure SDK team confirms it directly: abandoning re-serves the same message, and deferral *removes* the message from the session so the next receive hands you `A2`.

So there is one ordering authority for failure paths: the hold-back we store in session state. The session's FIFO owns "serialize within a healthy stream." The hold-back owns "serialize across a failure gap." Neither replaces the other.

That reframes what the session is actually buying us in this design. Its marginal value is not the failure-path ordering — we own that. It is that the exclusive lock makes the hold-back a **single-writer decision** instead of a distributed lease per `SessionId` that we would have to renew and recover on our own. `RequiresSession` provides the lock and state co-location; the hold-back provides failure-path ordering.

Because the hold-back is the failure-path ordering guarantee, its correctness is the ordering correctness. The broker does not validate that application rule.

## Session state

The transport needs ASB session state to track blocked sessions. That does not mean we should take the whole session state away from users.

Users may want to store lightweight session-related information there as well. We can support that if we treat the session state as a versioned envelope.

For example:

```json
{
  "version": 6,
  "transport": {
    "blocked": true,
    "logicalMessageId": "nservicebus-message-id",
    "expectedRetryMessageId": "delayed-7A9C...",
    "blockedAt": "2026-06-27T12:00:00Z",
    "retryAfter": "2026-06-27T12:00:06Z",
    "lastPeekedSequenceNumber": 15
  },
  "user": {
    "contentType": "application/json",
    "type": "MyEndpoint.CustomerSessionState, MyAssembly",
    "data": {}
  }
}
```

The transport owns the `transport` section. User code owns the `user` section. The `version` field lets us evolve the transport section without breaking existing state.

I would avoid exposing raw `SetSessionStateAsync(BinaryData)` as the normal API because that would let handlers accidentally overwrite transport metadata. Instead, we can expose a small abstraction through the pipeline context:

```csharp
public interface IAzureServiceBusSessionState
{
    Task<T?> Get<T>(CancellationToken cancellationToken = default);
    Task Set<T>(T state, CancellationToken cancellationToken = default);
    Task Clear(CancellationToken cancellationToken = default);
}
```

Example usage:

```csharp
public async Task Handle(MyMessage message, IMessageHandlerContext context)
{
    var sessionState = context.Extensions.Get<IAzureServiceBusSessionState>();

    var state = await sessionState.Get<CustomerState>(context.CancellationToken)
        ?? new CustomerState();

    state.ProcessedMessages++;

    await sessionState.Set(state, context.CancellationToken);
}
```

The production abstraction must read the envelope, update only the `user` section, and write it back. Transport recoverability must update only the `transport` section and preserve user state. The spike models only transport state and is not an implementation of this public API; user-state preservation remains an open production item.

## Recoverability

The core strategy is scheduled resend plus a peek-and-search hold-back. The restart assertion covers recovery after a clean checkpoint; hard-kill and ambiguous broker outcomes remain outside its scope.

Core delayed retries are still a problem for sessions. When Core delayed retries are used today, the failed message is completed and a copy is scheduled for later. That means later messages in the same session can be processed before the failed message comes back. For ordered sessions, that is not OK. So for session-enabled endpoints, recoverability needs to be session-aware.

### What we decided: scheduled resend + peek-and-search hold-back

On failure of a message `M` in session `S`, the implementation uses this order:

1. Create a distinct physical retry `MessageId` from the session, stable logical message identity, and retry attempt. Determinism gives the attempt a stable identity and can suppress duplicate sends outside or around orchestration; it is not needed to repair a partial same-entity transaction.
2. Schedule a fresh copy with the same `SessionId`, the new physical `MessageId`, the stable logical identity, and `ScheduledEnqueueTime = now + delay`.
3. Persist session `S` as blocked (`LogicalMessageId`, `ExpectedRetryMessageId`, and `RetryAfter`).
4. Complete the original. The scheduled copy is the retry.
5. Release the session.

The input queue has duplicate detection enabled. A retry must use a distinct physical broker `MessageId`; reusing the original would let duplicate detection discard the required scheduled retry as the original. Determinism is useful for stable attempt identity and for suppressing duplicate sends outside or around orchestration, but the same-entity transaction is all-or-none and does not rely on duplicate detection to repair a partial schedule/state/complete transition. The stable logical identity is what an outbox or idempotent handler uses across physical attempts.

The hold-back checks `RetryAfter` after accepting a session. Scheduled messages can be peeked while scheduled, but cannot be received before their due time. This implementation intentionally waits until due before scanning for `ExpectedRetryMessageId`:

```text
not due yet   -> after acceptance, cooldown until RetryAfter, release (no scan)
due, not found -> scan from the persisted frontier, persist the new frontier,
                  cooldown briefly, release
found         -> receive forward past the backlog, leaving it LOCKED but unsettled,
                 process ONLY the expected physical retry, clear block on success
after clear   -> the locked backlog re-flows on session close, in original order
```

A deliberate choice: the hold-back **never abandons** the backlog prefix. Abandoning re-serves the same messages to the head, so a retry that fails again re-abandons the same prefix on every pass and inflates `DeliveryCount` until the backlog dead-letters without ever being processed. Instead we receive forward past the backlog and close the session with the messages unsettled — per ASB session semantics, closing a session with unsettled messages re-flows them **without** incrementing `DeliveryCount` (the increment only happens on lock *expiry* or explicit abandon). So a multi-retry failure re-runs the hold-back with zero delivery churn. Receiving forward (rather than one fixed-size batch) is also what lets the hold-back reach a retry buried deeper than a single batch — the abandoned-prefix approach could never get past the re-flowed head.

The operation order is schedule → persist blocked session state → complete the original, and all three broker operations run inside one **same-entity Service Bus transaction**. The broker outcome is all-or-none:

- **Rollback:** the original remains available after its lock is released. No partial schedule/state/complete transition is exposed.
- **Commit:** the scheduled copy, blocked state, and original completion become visible together.
- **Ambiguous commit acknowledgement:** the process cannot know which outcome occurred, but the broker does not expose a partial transition. If orchestration repeats a schedule outside or around the transaction, a deterministic physical ID can let duplicate detection collapse it within its history window.

The transaction is limited to Service Bus operations. Azure Service Bus expires it two minutes after the first broker operation, SDK operations inside it are not retried automatically, and handler or database side effects remain outside the transaction. Those side effects still need idempotency or an outbox. Session release can still fail or the process can stop before release; the broker eventually releases the session lock and unsettled messages become available again, while the committed scheduled copy and session state remain.

This is the intended at-least-once behavior: preserve the original until the transaction commits, separate broker deduplication from logical-message idempotence, and treat ambiguous acknowledgement as an unknown outcome rather than a partial one. The executable scenarios assert clean-checkpoint recovery; hard-kill safety and ambiguous broker outcomes remain open.

### Why scheduled resend, and not defer

Deferral is not a complete ordering strategy because it still requires a hold-back, and it has a difficult restart story.

Defer is a nice storage primitive for the failed message during the delay, but "defer strictly preserves ordering" is not literally true, and treating it as true is the trap. Deferral *removes* the message from the session and sets it aside; the next receive hands you `A2`. Ordering of `A1` relative to the backlog is preserved not by defer but by our pump refusing to process `A2` until `A1` is recalled. So defer still needs the same hold-back. Given that, the comparison comes down to durability, and there defer loses hard.

The Azure SDK team confirms two facts (issues #16447 and #30252) that combine into silent data loss:

- Deferred messages do **not** expire to the DLQ while deferred. They only reach the DLQ when someone *attempts to receive them* after expiry.
- `AcceptNextSessionAsync` does not return a session whose only message has been deferred — ASB treats it as idle. So recall has to be driven by an in-memory registry plus `AcceptSessionAsync(sessionId)` by name.

Put those facts together and a process restart can leave the deferred message in the broker with no TTL rescue and no DLQ path until somebody receives it. A transport that requires durable recovery should not use deferral as its delayed-retry foundation.

Scheduled resend removes the in-memory registry problem. The scheduled message is normal broker state and survives a restart without a registry. The schedule, state write, and completion are one same-entity transaction, so an ambiguous acknowledgement leaves the process unsure whether the all-or-none transition committed, not with a partially committed transition. The peek-and-search hold-back it needs is something we have to build **anyway**, because ServiceControl retries arrive as normal messages behind the backlog — there is no "recall by sequence number" path for an external retry.

So scheduled resend is the core. The retried message gets a fresh physical `MessageId`, sequence number, enqueue time, and `DeliveryCount`, and it costs one extra send per retry. The stable logical identity and durable `RetryCount` travel as application properties instead of relying on broker identity or delivery count.

The hold-back persists `LastPeekedSequenceNumber`, not the value returned by `ScheduleMessageAsync`. A duplicate schedule request may be accepted and discarded by duplicate detection, so its returned sequence number cannot safely describe the retained retry. Starting from the head and persisting only active sequence numbers actually observed by peek avoids that ambiguity. A scheduled message may be peeked while scheduled, but activation appends it with a new final sequence number; the persisted active frontier therefore cannot skip the activated retry. The next pass, including one after restart, resumes after that frontier.

### Where hold-and-sleep still fits

For short delays — I'd say up to about two or three seconds — holding the session lock and sleeping is cheaper than the resend bookkeeping: no abandon churn, no extra send, ordering trivially preserved because `A2…A4` stay locked behind `A1`, and `DeliveryCount` is not burned by the delay. My current leaning is a layered design:

- up to ~2–3s: hold-and-sleep
- longer, in-process: scheduled resend + peek-and-search hold-back
- cross-process / long delay / external: ServiceControl retry, which is just another normal-message resend under the same hold-back

### Why re-enqueue-all is not the primary path

Re-enqueueing every observed backlog message only preserves order in a drained, quiescent session. A concurrent producer can continue adding messages, and the operation is `O(backlog)` mutation to recreate an ordering property the hold-back provides without moving messages. It is an operational escape hatch for a quiescent session, not the primary path.

## What needs to change in the code

### Queue creation

`AzureServiceBusTransport.DetermineQueuesToCreate` currently creates normal queues. Session mode needs to set `RequiresSession = true` and enable duplicate detection on endpoint input queues. The duplicate-detection history window must cover the retry scheduling and ambiguous-outcome recovery window the transport wants the broker to collapse.

Both settings affect entity creation, so existing queues need validation and a migration path.

### Input queue pump

`MessagePump` currently uses a regular processor:

```csharp
serviceBusClient.CreateProcessor(...)
```

Session mode needs a session processor:

```csharp
serviceBusClient.CreateSessionProcessor(...)
```

The concurrency model also changes. Endpoint max concurrency does not map cleanly to sessions. We likely need to think in terms of max concurrent sessions and calls per session.

### Subscription creation

I think this should fall out of the endpoint's transport mode, not from a public per-route toggle.

If the endpoint has sessions enabled, subscriptions created for that endpoint should be session-enabled and should not use `ForwardTo`. The transport can still carry internal metadata that says "this subscription belongs to a session-enabled endpoint and needs a bridge", but I would avoid exposing this as something users set independently on each `SubscribeTo` route.

That distinction matters. A public `SubscribeTo(..., requiresSession: true)` option suggests that a user can make one subscription session-enabled while the endpoint input queue remains normal. Technically we could bridge that, but the resulting guarantee is much weaker and easy to misunderstand.

So the initial rule is:

> Sessions are an endpoint/transport mode. If sessions are enabled for the endpoint, the endpoint input queue and its subscriptions participate in that mode. If sessions are not enabled for the endpoint, the endpoint cannot subscribe to session-enabled subscriptions.

A session-enabled endpoint should also only receive messages that have a `SessionId`.

We can relax this later if there is a good use case, but I would not start with mixed semantics.

### Subscription metadata

`SubscriptionEntry` is already a richer value type:

```csharp
public readonly record struct SubscriptionEntry(string Topic, TopicRoutingMode? RoutingMode = null)
```

I would not add a public `RequiresSession` flag to this type as part of the first design. That would make sessions look like a routing concern, while the stronger model is that sessions are an endpoint transport mode.

Internally, the topology code still needs to know whether it is provisioning subscriptions for a session-enabled endpoint. That can come from the transport/session configuration passed into subscription creation rather than from user-authored routing metadata.

### SessionId propagation

The send path does not currently set native `SessionId`.

We need:

- send/reply options to set `SessionId`
- a dispatch property for `SessionId`
- propagation from incoming session messages to outgoing messages where appropriate
- access to the incoming native `SessionId` from the pipeline
- ServiceControl retry to preserve `SessionId`

The last point is a hard dependency for the hold-back: if ServiceControl ever stopped preserving the original `SessionId` (the AMQP `GroupId`), a bring-back would not correlate and the whole recoverability story would break.

### Subscription bridge

We need a new transport-owned component that manages ordered subscription processors.

It needs to:

- start and stop with the endpoint
- receive from session-enabled subscriptions
- send copies to the input queue
- preserve `SessionId`
- settle source messages safely
- integrate with critical errors and diagnostics

The receive-send-complete sequence uses cross-entity transactions: receive from a session-enabled subscription in peek-lock, open a transaction scope, send the copy and complete the source in the same transaction, and preserve `SessionId`. The executable scenarios do not inject bridge rollback, so rollback behavior remains an explicit fault-injection item. For partitioned/session entities, `SessionId` acts as the partition key, so the forwarded message must use a compatible `SessionId`/partition key.

## What the executable scenarios assert

The executable scenarios cover normal processing, a backlog deeper than one peek page, two competing pumps, and restart with a fresh in-memory cooldown map. They enable duplicate detection, derive retry attempts from durable metadata, fail closed on unreadable session state, and record a successful handler completion only after message settlement or transaction disposal. The observer records session ID, logical ID, physical ID, and attempt, then uses bounded polling and a short quiescence check.

- Normal and competing scenarios assert exactly-once completion for the listed scenario messages in per-session order. They require Customer-123 attempt 2 before `msg3` and concurrent messages, and Customer-789 success on attempt 3.
- Stretch asserts Customer-123 attempt 2 before all 40 held backlog messages and the remaining Customer-123 messages.
- Restart asserts that a fresh pump clears the durable block and completes Customer-123 as `msg1`, `msg2` attempt 2, `msg3`, alongside the expected fixed messages in their own sessions.
- Terminal mode drives one message through `MaxAttempts` into the DLQ, keeps later same-session messages held, then asserts the deterministic manual physical ID, preserved durable retry/manual counters, successful manual completion, exact per-session order, and no duplicate completion records. The simulator distinguishes the first manual recovery cycle from durable message metadata rather than a process-local transport switch.
- The observer's claims are limited to the asserted scenario messages. It is scenario instrumentation, not transport correctness; no global order is asserted across sessions, subscriptions, or topics.

The normal, stretch, competing, restart, and terminal modes pass these executable assertions. They do not inject bridge rollback, hard kills, or ambiguous broker acknowledgements, so those behaviors remain open.

## What remains open

- **Least-bad blocked-session strategy for the asserted path.** Scheduled resend plus peek-and-search hold-back is the implemented strategy. Deferral has a difficult restart story; re-enqueue-all cannot reliably beat a concurrent producer; hold-and-sleep remains useful for short delays. Hard-kill and ambiguous broker outcomes still need fault-injection tests.
- **Retry identity and frontier.** The hold-back stores the stable logical identity for idempotence and the expected physical retry `MessageId` for broker lookup. Each intended retry attempt has a distinct physical identity so duplicate detection does not discard the retry as a duplicate of the original. Determinism is useful for stable attempt identity and duplicate suppression outside or around orchestration; the same-entity transaction is all-or-none and does not rely on duplicate detection to repair a partial schedule/state/complete transition. `SequenceNumber` is neither identity nor a persisted lower bound; the scan persists only active sequence numbers it has actually peeked.
- **Migration story — OPEN.** `RequiresSession` cannot be flipped on an existing entity. Fail fast, manual migration, a helper, or new entity names — still a decision.
- **Mixed ordered/unordered messages — DECIDED, start with no.** With Core 10.2 supporting multiple endpoints in one process and the newer throughput-based licensing, hosting two endpoints (one session-enabled, one regular) is a cleaner escape hatch than complicating the transport.
- **Session-enabled subscriptions on non-session endpoints — DECIDED, start with no.** We could bridge into a normal queue, but then we only preserve order until the bridge and the guarantee becomes subtle and dangerous.

Still-open implementation and posture items:

- **Hard-kill and ambiguous-outcome restart variant.** The executable restart assertion covers a clean checkpoint. Client recovery when a process stops during the same-entity transaction or when its commit acknowledgement is ambiguous remains open. Fault injection is needed for client recovery and handler/database side effects outside the transaction, not to test a partial broker schedule/state/complete transition: the broker should expose either the rolled-back original or the committed scheduled copy, blocked state, and completion. A process restart still cannot infer which outcome occurred from an ambiguous acknowledgement, and the duplicate-detection window still needs testing.
- **Bridge back-pressure escape valve.** During destination failure, the processor can hold a bounded active source delivery; repeated failures and redeliveries can consume delivery attempts and eventually dead-letter the source message. No deliberate production escape or critical-error policy exists yet. ASB's `ForwardTo` dead-letters at the source to protect the topic; the bridge needs an explicit policy.
- **TTL interaction with the hold-back.** For session-enabled entities, if any message's TTL expires, ASB drops or dead-letters **all** messages in the session. A delayed-retry loop that holds `A2…A4` while `A1` is pending is a slow path to that trigger — one expiring message takes the whole session down. Hold-back duration and per-message TTL need to be budgeted together.
- **`MaxDeliveryCount` semantics.** The hold-back no longer abandons the backlog (it re-flows via session close without incrementing `DeliveryCount`), so the broker's `MaxDeliveryCount` is not burned by hold-back churn. It still matters for immediate abandon-retries and lock expiry. Delayed attempts are read from the durable `RetryCount` application property, not an in-memory counter.
- **ServiceControl bring-back: same strict hold-back, distinct identity.** A bring-back receives a new physical broker identity and preserves the same stable logical identity. Terminal failure releases the session lock and receiver but, in the same-entity transaction, keeps the session durably blocked while recording the exact deterministic manual `ExpectedRetryMessageId` and durable manual-retry counter. The unsettled backlog becomes available in its existing order; a later receiver reads the block and keeps it from the handler. The DLQ processor preserves message metadata and counters, then uses a cross-entity transaction to send that physical identity to the input queue and complete the DLQ message. The hold-back selects it by physical identity before releasing the older backlog to the handler; its new `SequenceNumber` and queue position are irrelevant.
- **Operator discard/unblock limitation.** Automated or manual retry exhaustion fails closed: the DLQ evidence and blocked session remain until an explicit discard/unblock action. That action intentionally skips the failed message, after which the remainder retains relative session order. This spike does not implement the full operator control path; that is an operational limitation, not an undecided ordering policy.
- **Session state `user` section + `IAzureServiceBusSessionState` API.** The production envelope must preserve user-owned state. This spike intentionally models only transport state and is not an implementation of that public API.

## Assumptions I do not want us to gloss over

The direction fits the Service Bus constraints only if these edges remain explicit.

What seems solid:

- session-enabled subscriptions cannot use `ForwardTo`, so the current auto-forwarding topology cannot directly support ordered subscriptions
- `RequiresSession` is decided when the entity is created, so we need validation or a migration story for existing queues and subscriptions
- the input queue is the right place to centralize handler execution, recoverability, blocked-session state, and ServiceControl retry behavior
- Core delayed retries are not compatible with strict session ordering because they complete the failed message and schedule a copy — so we replace them with transport-owned scheduled resend
- moving a failed message to the error queue requires preserving the session block and expected manual physical identity so the same hold-back can order its bring-back
- the hold-back matches the expected physical retry identity and is what preserves order across a failure
- unreadable or unsupported session state fails closed instead of being treated as an unblocked session
- delayed retry attempts come from durable message metadata rather than process memory

What still needs care:

- The retry transition is intentionally schedule → persist blocked session state → complete the original inside one same-entity Service Bus transaction. A rollback leaves the original available; a commit exposes the scheduled copy, blocked state, and completion together. An ambiguous acknowledgement leaves the outcome unknown to the process, not partially committed at the broker. Broker duplicate detection can suppress repeated sends of one deterministic physical attempt outside or around orchestration; logical-message idempotence handles handler or database side effects outside the transaction. The executable scenarios do not fault-inject those boundaries.
- The bridge preserves the order it receives from one session-enabled subscription, but it cannot create a global order across independent sources. That is fine because raw Service Bus sessions do not provide it either.
- The bridge requires receive-send-complete to be atomic. Rollback and duplicate/loss cases need explicit fault-injection coverage before production claims are made.
- For partitioned entities, `SessionId` is also the partition key. Any `PartitionKey` or `TransactionPartitionKey` we set must be compatible with it.
- Session state persists after all messages in the session are consumed, counts against the entity quota, and needs cleanup. The size limit also depends on the tier.
- A session-enabled endpoint cannot receive messages without `SessionId`. We should fail fast before sending or publishing such messages into a session-enabled path.
- Batching needs a closer look. Today the dispatcher batches by destination. In session mode, batching may need to group by destination and session, or at least make sure transaction and partition requirements are not violated.
- `TransportTransactionMode.None` probably does not fit strict session mode because receive-and-delete removes the ability to abandon, defer, complete, or dead-letter as part of recoverability.

So the promise stays narrow:

> We preserve ordered endpoint processing per session at the session-enabled input queue boundary. Ordered subscriptions are bridged into that boundary. We do not claim global ordering across independent sources because Azure Service Bus sessions do not provide that either. The executable scenarios assert the scheduled-resend hold-back and graceful restart after a clean checkpoint for listed messages. The remaining unknowns are bridge rollback, hard-kill and ambiguous broker outcomes, the back-pressure valve, and the unblock-strategy posture.

## Suggested decision

Use the session-enabled input queue as the central design point:

- do not consume ordered subscriptions directly into handlers
- use subscription session processors only as transport-owned bridges
- implement session-aware recoverability once, in the input queue session pump, using a stable logical identity plus an expected physical retry `MessageId`
- enable duplicate detection and use deterministic per-attempt physical identities so ambiguous schedule retries can be collapsed safely
- use ASB session state for transport blocking metadata, versioned envelope, transport/user split
- allow users to use session state through the safe envelope-based abstraction

This is a sizeable change rather than a small topology-creation adjustment. It keeps the model close to the endpoint boundary expected by NServiceBus while preserving a narrow per-session guarantee.

## Proposed next step

The remaining work is transport-facing implementation and two posture decisions:

1. The **unblock strategy** (clear-and-flow vs hold-until-manual vs hold-with-timeout, plus the control message). This changes what users observe, so I'd want it decided before we lock the recoverability contract.
2. The **migration story** for existing entities, since `RequiresSession` is creation-time only.

The remaining implementation gaps are the bridge back-pressure valve, TTL/hold-back budgeting, hard-kill and ambiguous-outcome fault injection (including duplicate-detection-window expiry), and the `IAzureServiceBusSessionState` abstraction.

## References

- Azure SDK issue #16447 — deferral does not block the session; the next message is served.
- Azure SDK issue #30252 — deferred messages do not expire to the DLQ; recoverable only by peek; `AcceptNextSessionAsync` will not surface deferred-only sessions.
- MS Learn, message-sessions — abandoning re-serves the same message; `MaxDeliveryCount` semantics; TTL drops or dead-letters the whole session on session-enabled entities.
- MS Learn, duplicate detection — scheduled messages participate in duplicate detection; the history window controls how long repeated physical sends are discarded.
- MS Learn, auto-forwarding — "Service Bus bills one operation for each forwarded message"; autoforwarding is not supported for session-enabled entities; destination-quota failure dead-letters at the source.
- MS Learn, [message sequencing and timestamps](https://learn.microsoft.com/azure/service-bus-messaging/message-sequencing) — a scheduled message's sequence number is valid only while it is scheduled; activation appends the message with a new sequence number.
- MS pricing FAQ — operations metering: each API interaction (send/receive/complete/renew-lock/session-state) counts, in 64 KB message granularity.
- Spike code: `Program.cs`, `Prepare.cs` in this project.
