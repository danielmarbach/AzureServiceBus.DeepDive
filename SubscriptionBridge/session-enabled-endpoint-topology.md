# Session-enabled endpoints with ordered subscriptions

> Status note: this started as a design proposal and then became the record of a spike that tested it. The proposal sections are mostly still as I first wrote them, because the reasoning still holds. The parts the spike actually settled — mostly recoverability — are updated, and there's a section at the end with what we proved and what I think is still open. Please don't hesitate to challenge any of it.

## Short version

I think we should avoid consuming ordered subscriptions directly into handlers.

My current leaning — and now also what the spike backs up — is that, when sessions are enabled, the endpoint input queue should become the ordered processing boundary. Ordered subscriptions are still consumed by the transport, but only as a bridge into the endpoint input queue.

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

I might still be missing something, but from where I stand this seems like a cleaner model than making subscriptions firstclass receive endpoints, and the spike didn't surface anything that changes that.

## Why I think the current direction feels off

Azure Service Bus does not allow auto-forwarding from a session-enabled subscription. That part is not really up for debate.

The conclusion I am less convinced about is that we therefore need to consume directly from subscriptions into the endpoint pipeline, or force users into many small endpoints for every ordered event group.

That feels like we would be letting an Azure Service Bus infrastructure limitation leak too far into the NServiceBus programming model.

Today our topology is built around this model:

```text
topic subscription -> ForwardTo -> endpoint input queue -> endpoint pump
```

The code reflects that. We create subscriptions with `ForwardTo = inputQueue` in:

- `src/Transport/EventRouting/TopicPerEventTopologySubscriptionManager.cs`
- `src/Transport/EventRouting/MigrationTopologySubscriptionManager.cs`

That gives us a nice property: the endpoint has one input queue that represents its work. Recoverability, ServiceControl retry, monitoring, and operational visibility all line up around that queue.

I think we should preserve that property if we can.

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

The input queue session pump is where we put the recoverability and blocking logic.

## Why this seems better

### We keep the endpoint model intact

Users still have an endpoint input queue, and I think that matters.

If a message fails, gets moved to the error queue, is picked up by ServiceControl, and is later retried, it naturally comes back to the endpoint input queue. That is the place where we can inspect the session state and decide whether this message unblocks the session.

If we consume directly from subscriptions, we now have several receive points that all need to understand recoverability, blocking, retries, concurrency, and shutdown. I am not convinced that is worth it.

### We relocate back-pressure to the right place

This is an argument I think is underweighted. Without `ForwardTo`, messages sit in the subscription until the bridge drains them, and subscriptions count against the **topic's** size quota. A stalled session subscription can push the whole topic toward quota and throttle publishers across every subscription on that topic.

The bridge drains into the endpoint input queue, so the buffering lives on the **endpoint queue's** quota instead. That is where I want the capacity decision to live: the endpoint owns its own pressure, and one stalled endpoint no longer starves sibling subscribers on the same topic.

I don't want to oversell this — we are relocating back-pressure, not removing it. If the input queue rejects the send, the cross-entity transaction aborts and the pressure still propagates back to the topic. And ASB's own `ForwardTo` failure mode dead-letters at the source to protect the topic; our bridge does not have that escape valve yet. I think we need one. That is in the open-items list.

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

I would much rather have that decision in one input queue pump than repeat it across direct subscription pumps.

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

I think that is fine. It is probably the only meaningful guarantee we can make without inventing a distributed ordering coordinator on top of Service Bus.

## The mental model that sharpened during the spike

I want to call this out explicitly because it changed how I think about the recoverability section.

A session gives us two things, and only two: happy-path FIFO (when nothing fails, `A1` is delivered and completed before `A2` is handed over), and an exclusive lock with colocated state (one receiver owns the session, and there is a place to stash metadata).

It does **not** give us ordering across a failure. The moment `A1` fails and we want anything other than instant abandon-and-reserve, ordering is gone from the broker's point of view and becomes our problem. This is not a guess — the Azure SDK team confirms it directly: abandoning re-serves the same message, and deferral *removes* the message from the session so the next receive hands you `A2`.

So there is one ordering authority for failure paths: the hold-back we store in session state. The session's FIFO owns "serialize within a healthy stream." The hold-back owns "serialize across a failure gap." Neither replaces the other.

That reframes what the session is actually buying us in this design. Its marginal value is not the failure-path ordering — we own that. It is that the exclusive lock makes the hold-back a **single-writer decision** instead of a distributed lease per `SessionId` that we would have to renew and recover on our own. I'd keep `RequiresSession` for the lock and the state co-location, and stop crediting it for failure-path ordering it does not provide.

The practical consequence: because the hold-back *is* the ordering guarantee, its correctness *is* our ordering correctness. The broker will not catch a mistake here. I think we should test the hold-back as if there were no session underneath, because for the paths that matter, there effectively isn't.

## Session state

The transport needs ASB session state to track blocked sessions. That does not mean we should take the whole session state away from users.

Users may want to store lightweight session-related information there as well. We can support that if we treat the session state as a versioned envelope.

For example:

```json
{
  "version": 3,
  "transport": {
    "blocked": true,
    "blockedMessageId": "nservicebus-message-id",
    "blockedAt": "2026-06-27T12:00:00Z",
    "retryAfter": "2026-06-27T12:00:06Z"
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

The abstraction reads the envelope, updates only the `user` section, and writes it back. Transport recoverability does the inverse: update only the `transport` section and preserve user state. This is sketched, not built — it's in the open-items list.

## Recoverability

This is the section the spike actually settled, and it moved further than I expected. I'm keeping my original framing and then updating it with what we learned.

Core delayed retries are still a problem for sessions. When Core delayed retries are used today, the failed message is completed and a copy is scheduled for later. That means later messages in the same session can be processed before the failed message comes back. For ordered sessions, that is not OK. So for session-enabled endpoints, recoverability needs to be session-aware.

### What we decided: scheduled resend + peek-and-search hold-back

On failure of a message `M` in session `S`:

1. Mark session `S` blocked in session state (`BlockedMessageId = M.MessageId`, `RetryAfter = now + delay`).
2. Schedule a fresh copy with the **same `SessionId` and same `MessageId`**, `ScheduledEnqueueTime = now + delay`.
3. Complete the original — the scheduled copy *is* the retry.
4. Release the session.

The hold-back, on every accept of a blocked session, peeks a window and searches for `BlockedMessageId`:

```text
not found   -> retry not yet visible -> cooldown until RetryAfter, release
found       -> receive forward past the backlog, leaving it LOCKED but unsettled,
               process ONLY the match, clear block on success
after clear -> the locked backlog re-flows on session close, in original order
```

A deliberate choice: the hold-back **never abandons** the backlog prefix. Abandoning re-serves the same messages to the head, so a retry that fails again re-abandons the same prefix on every pass and inflates `DeliveryCount` until the backlog dead-letters without ever being processed. Instead we receive forward past the backlog and close the session with the messages unsettled — per ASB session semantics, closing a session with unsettled messages re-flows them **without** incrementing `DeliveryCount` (the increment only happens on lock *expiry* or explicit abandon). So a multi-retry failure re-runs the hold-back with zero delivery churn. Receiving forward (rather than one fixed-size batch) is also what lets the hold-back reach a retry buried deeper than a single batch — the abandoned-prefix approach could never get past the re-flowed head.

Write-ordering on failure matters: mark blocked first (durable), then schedule, then complete. If we crash after marking blocked, the original re-delivers on lock expiry and the pump re-processes it under the block — correct.

### Why scheduled resend, and not defer

I originally leaned toward defer because it is the API that strictly preserves the backlog's order without re-enqueueing anything. The spike talked me out of it, and I want to show the reasoning because it is the load-bearing decision in this doc.

Defer is a nice storage primitive for the failed message during the delay, but "defer strictly preserves ordering" is not literally true, and treating it as true is the trap. Deferral *removes* the message from the session and sets it aside; the next receive hands you `A2`. Ordering of `A1` relative to the backlog is preserved not by defer but by our pump refusing to process `A2` until `A1` is recalled. So defer still needs the same hold-back. Given that, the comparison comes down to durability, and there defer loses hard.

The Azure SDK team confirms two facts (issues #16447 and #30252) that combine into silent data loss:

- Deferred messages do **not** expire to the DLQ while deferred. They only reach the DLQ when someone *attempts to receive them* after expiry.
- `AcceptNextSessionAsync` does not return a session whose only message has been deferred — ASB treats it as idle. So recall has to be driven by an in-memory registry plus `AcceptSessionAsync(sessionId)` by name.

Put those together and the failure mode is: process restarts, the in-memory registry is gone, the deferred message sits in the broker with no TTL rescue and no DLQ path until somebody receives it. For a transport where "we do not lose messages" is the baseline posture, I think that is a production blocker. We saw exactly this in the spike — a single-message deferred session was never recalled until we added a by-name registry, and even then the registry dying on restart strands the message.

Scheduled resend has none of that fragility. The scheduled message is normal broker state: it survives restart for free, needs no registry, and has no orphan risk. And the peek-and-search hold-back it needs is something we have to build **anyway**, because ServiceControl retries arrive as normal messages behind the backlog — there is no "recall by sequence number" path for an external retry.

So scheduled resend is the core. The honest trade-offs: the retried message gets a fresh sequence number, fresh enqueue time, and reset `DeliveryCount` each time, and it costs one extra send per retry. We carry the attempt count as an application property (`RetryCount`) rather than relying on the broker's delivery count.

One refinement that fell out of looking closer at the hold-back: `ScheduleMessageAsync` returns the scheduled message's sequence number, and we persist that in session state alongside the block. So when the pump comes back to a blocked session, it can address the retry directly with a one-message peek instead of scanning the backlog — `O(1)` instead of `O(backlog)`. That makes the large-backlog correctness concern go away entirely for pump-scheduled retries. The scan stays as a fallback for the two cases where the stored seq is unknown or stale: a crash between schedule and state write, and an external ServiceControl retry (we didn't schedule that one, so there's no seq to address). The seq survives restart because it lives in session state, and sessions are exclusive-lock, so even with multiple pump instances the instance that picks up the session on restart reads the same state — scale-out isn't a problem for the fast path.

### Where hold-and-sleep still fits

For short delays — I'd say up to about two or three seconds — holding the session lock and sleeping is cheaper than the resend bookkeeping: no abandon churn, no extra send, ordering trivially preserved because `A2…A4` stay locked behind `A1`, and `DeliveryCount` is not burned by the delay. My current leaning is a layered design:

- up to ~2–3s: hold-and-sleep
- longer, in-process: scheduled resend + peek-and-search hold-back
- cross-process / long delay / external: ServiceControl retry, which is just another normal-message resend under the same hold-back

### Why we rejected re-enqueue-all

We also explored "re-enqueue every backlog message we see until the delayed `A1` arrives." I want to record why we said no, so it does not come back. It only preserves order in a drained, quiescent session: against a concurrent producer still sending `A5`, `A6`, we race the producer and can never win. It is also `O(backlog)` mutation to recreate an ordering property the hold-back gives for free by not moving anything. Keep it only as a recovery escape hatch on quiescent sessions, not as the primary path.

## What needs to change in the code

### Queue creation

`AzureServiceBusTransport.DetermineQueuesToCreate` currently creates normal queues. Session mode needs to set `RequiresSession = true` on endpoint input queues.

That is a creation-time ASB setting, so existing queues cannot be converted in place.

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

The receive-send-complete sequence uses cross-entity transactions. The spike confirms the wiring works: receive from a session-enabled subscription in peek-lock, open a transaction scope, complete the source and send the copy in the same transaction, preserve `SessionId`, and rollback behaves correctly when either side fails. For partitioned/session entities, `SessionId` acts as the partition key, so the forwarded message must use a compatible `SessionId`/partition key.

## What the spike proved

All of this is against a live namespace, clean exit, no errors unless noted. The code is in `Program.cs` / `Prepare.cs` in this project.

- **The bridge wires correctly.** Cross-entity transactional forwarding from two independent session-enabled subscriptions into one shared session-enabled input queue. No duplicates, no leaks.
- **Multiple ordered subscriptions into one input queue preserve per-session order.** Each subscription keeps its own per-session order; cross-subscription order is correctly not claimed.
- **Scheduled-resend delayed retries preserve order under a concurrent producer.** With `Customer-123`, `msg2` failed and was scheduled for a +6s resend while a concurrent publisher kept feeding `msg5/6/7` into the same session. Completion order came out `msg1 -> msg2 -> msg3 -> msg5 -> msg6 -> msg7`. The hold-back held the backlog prefix (locked, unsettled) until the scheduled `msg2` arrived, then re-flowed it on session close. That is the falsifiable test, and it held.
- **Multi-retry works.** A message that needed two scheduled retries completed on the third attempt; the hold-back held across both delays. This is the case defer stranded, and scheduled resend handled it natively.
- **Restart recovers from durable state.** We tore down the pump and client while a block was in flight, sat with no pump past the retry delay (wiping the in-memory cooldown), and started a fresh pump on a new client. It reconstructed everything from session state + the scheduled broker message, and order still held. Caveat: that was a graceful stop, not a hard kill mid-write — see open items.

The one result worth flagging as a failure, because it is what localized the hardest bug: an earlier version of the spike cleared the block unconditionally on a ServiceControl-style resend, and `msg3` (Shipping) completed before `msg2` (Payment). Everything was durable; the logic was wrong. That run is the reason I keep saying the hold-back *is* the ordering guarantee.

## What the spike resolved, and what is still open

The original list of things to spike, with outcomes:

- **Bridge transaction wiring — RESOLVED.** Cross-entity transactions work with session processors; rollback behaves.
- **Least-bad blocked-session strategy — RESOLVED.** Scheduled resend + peek-and-search hold-back. Defer rejected (restart strands messages); re-enqueue-all rejected (can't beat a concurrent producer); hold-and-sleep kept for short delays.
- **How a retried message unblocks a session — RESOLVED, and differently than I first guessed.** I originally thought the broker `SequenceNumber` would be the blocking identity. The spike says no: sequence number does not survive a ServiceControl retry (the copy is re-enqueued with a new one), and tracking it for defer is the thing that strands messages on restart. `MessageId` is the durable key, because we control it and it survives every hop. That is why the hold-back matches on `MessageId`, not sequence number.
- **Migration story — OPEN.** `RequiresSession` cannot be flipped on an existing entity. Fail fast, manual migration, a helper, or new entity names — still a decision.
- **Mixed ordered/unordered messages — DECIDED, start with no.** With Core 10.2 supporting multiple endpoints in one process and the newer throughput-based licensing, hosting two endpoints (one session-enabled, one regular) is a cleaner escape hatch than complicating the transport.
- **Session-enabled subscriptions on non-session endpoints — DECIDED, start with no.** We could bridge into a normal queue, but then we only preserve order until the bridge and the guarantee becomes subtle and dangerous.

Still-open implementation and posture items:

- **Hard-kill restart variant.** The clean-checkpoint restart is proven; a hard kill between *mark-blocked* and *schedule* is not, because a graceful stop drains in-flight work and cannot hit that window.
- **Bridge back-pressure escape valve.** On persistent send failure (N attempts or quota exception), the bridge currently just stops draining and pressure propagates to the topic. ASB's `ForwardTo` dead-letters at the source to protect the topic; I think our bridge should too.
- **TTL interaction with the hold-back.** For session-enabled entities, if any message's TTL expires, ASB drops or dead-letters **all** messages in the session. A delayed-retry loop that holds `A2…A4` while `A1` is pending is a slow path to that trigger — one expiring message takes the whole session down. Hold-back duration and per-message TTL need to be budgeted together.
- **`MaxDeliveryCount` semantics.** The hold-back no longer abandons the backlog (it re-flows via session close without incrementing `DeliveryCount`), so the broker's `MaxDeliveryCount` is not burned by hold-back churn. It still matters for the failed message itself if immediate abandon-retries ever exist, and for lock *expiry* on a hard kill — the transport owns attempt tracking via `RetryCount`.
- **ServiceControl bring-back: shared mechanism, distinct semantics.** From the hold-back's perspective a bring-back is just "a message with the blocked `MessageId` appears in the queue" — same peek-and-search as a scheduled resend, no new mechanism. The substantive point is ordering *after* terminal escalation: today the spike DLQs the message **and clears the block**, so the backlog drains immediately, and a later ServiceControl retry lands on an unblocked session and can process ahead of already-drained backlog. That is an order violation by construction, not by bug. I think it is a defensible posture (a message pushed to the error queue has left the ordered stream; a retry is an operational intervention, not part of the ordered contract), but it is a stance to take deliberately, not stumble into.
- **Session unblock strategy on terminal failure — open design dimension.** "Clear and flow" is not the only posture. We could *hold until manual unblock* (strict ordering, lower availability) or *hold with a bounded timeout* (a compromise). The mechanism already exists — the block is just session state — so what is missing is the policy toggle and, I think, a **control message** path: a well-known message the pump recognizes as "unblock this session," so operators do not have to touch ASB session state directly. A control message is also the natural way to *re-block* if a ServiceControl retry should land strictly in order behind a re-established hold. I'd want to design this before we lock the recoverability posture.
- **Session state `user` section + `IAzureServiceBusSessionState` API.** Sketched above, not built.

## Assumptions I do not want us to gloss over

I think the direction fits the Service Bus constraints, but only if we are honest about the edges.

What seems solid:

- session-enabled subscriptions cannot use `ForwardTo`, so the current auto-forwarding topology cannot directly support ordered subscriptions
- `RequiresSession` is decided when the entity is created, so we need validation or a migration story for existing queues and subscriptions
- the input queue is the right place to centralize handler execution, recoverability, blocked-session state, and ServiceControl retry behavior
- Core delayed retries are not compatible with strict session ordering because they complete the failed message and schedule a copy — so we replace them with transport-owned scheduled resend
- moving a failed message to the error queue loses broker ordering unless we mark the session as blocked
- the hold-back, not the session, is what preserves order across a failure

What still needs care:

- The bridge preserves the order it receives from one session-enabled subscription, but it cannot create a global order across independent sources. That is fine because raw Service Bus sessions do not provide it either.
- The bridge is only boring if receive-send-complete is atomic. The spike says it is, but we should keep the duplicate/loss cases in mind for the real transport's diagnostics.
- For partitioned entities, `SessionId` is also the partition key. Any `PartitionKey` or `TransactionPartitionKey` we set must be compatible with it.
- Session state persists after all messages in the session are consumed, counts against the entity quota, and needs cleanup. The size limit also depends on the tier.
- A session-enabled endpoint cannot receive messages without `SessionId`. We should fail fast before sending or publishing such messages into a session-enabled path.
- Batching needs a closer look. Today the dispatcher batches by destination. In session mode, batching may need to group by destination and session, or at least make sure transaction and partition requirements are not violated.
- `TransportTransactionMode.None` probably does not fit strict session mode because receive-and-delete removes the ability to abandon, defer, complete, or dead-letter as part of recoverability.

So the promise stays narrow:

> We preserve ordered endpoint processing per session at the session-enabled input queue boundary. Ordered subscriptions are bridged into that boundary. We do not claim global ordering across independent sources because Azure Service Bus sessions do not provide that either. The spike proves bridge atomicity and the scheduled-resend hold-back; the remaining unknowns are the hard-kill window, the back-pressure valve, and the unblock-strategy posture.

## Suggested decision

Use the session-enabled input queue as the central design point:

- do not consume ordered subscriptions directly into handlers
- use subscription session processors only as transport-owned bridges
- implement session-aware recoverability once, in the input queue session pump, using scheduled resend + peek-and-search hold-back keyed on `MessageId`
- use ASB session state for transport blocking metadata, versioned envelope, transport/user split
- allow users to use session state through the safe envelope-based abstraction

This is still a sizeable change. I do not think we should present it as a small adjustment to topology creation. But the spike makes me more confident it gives us a model that is easier to explain and much closer to how NServiceBus endpoints are expected to behave.

## Proposed next step

The original next step was a spike of a vertical slice. Most of that slice is now done: sessions on, session-enabled input queue, session processor consumption, `SessionId` on outgoing messages, one ordered subscription without `ForwardTo`, bridged in with `SessionId` preserved, ordered event handling, blocked-session metadata after a failure, and another session continuing while a failed session is blocked.

So I think the next step is the transport-facing work and the two posture decisions:

1. The **unblock strategy** (clear-and-flow vs hold-until-manual vs hold-with-timeout, plus the control message). This changes what users observe, so I'd want it decided before we lock the recoverability contract.
2. The **migration story** for existing entities, since `RequiresSession` is creation-time only.

After those, the implementation gaps: bridge back-pressure valve, TTL/hold-back budgeting, the hard-kill restart variant, and the `IAzureServiceBusSessionState` abstraction.

I'm quite aware this doc is long and that the spike is a spike, not done done. Happy to tighten any of it, and please don't hesitate to challenge the scheduled-resend decision or the unblock-strategy framing — those are the two places I'd most want a second opinion.

## References

- Azure SDK issue #16447 — deferral does not block the session; the next message is served.
- Azure SDK issue #30252 — deferred messages do not expire to the DLQ; recoverable only by peek; `AcceptNextSessionAsync` will not surface deferred-only sessions.
- MS Learn, message-sessions — abandoning re-serves the same message; `MaxDeliveryCount` semantics; TTL drops or dead-letters the whole session on session-enabled entities.
- MS Learn, auto-forwarding — "Service Bus bills one operation for each forwarded message"; autoforwarding is not supported for session-enabled entities; destination-quota failure dead-letters at the source.
- MS pricing FAQ — operations metering: each API interaction (send/receive/complete/renew-lock/session-state) counts, in 64 KB message granularity.
- Spike code: `Program.cs`, `Prepare.cs` in this project.
