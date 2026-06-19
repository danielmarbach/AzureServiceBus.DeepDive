#nullable enable

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using static System.Console;

namespace SubscriptionBridge;

// Spike: session-enabled subscription bridge + input queue session pump with
// blocked-session management.
//
// The shape we're validating:
//
//   publisher
//     -> topic (orders)
//       -> session-enabled subscription (sales-sub, no ForwardTo)
//         -> transport-owned subscription bridge (session processor)
//           -> session-enabled endpoint input queue (sales-input)
//             -> input queue session pump (manual AcceptNextSessionAsync)
//               -> simulated handler
//
// Two design decisions being stress-tested:
//
// 1. Cross-entity transactions — the bridge uses EnableCrossEntityTransactions +
//    TransactionScope to atomically forward from subscription to input queue.
//    Same pattern as the SendVia example in this repo.
//
// 2. Manual session acceptance — using AcceptNextSessionAsync instead of a
//    SessionProcessor for the input queue pump. The worry is that a SessionProcessor's
//    prefetch would waste I/O on blocked sessions. Manual acceptance gives us precise
//    control. Blocked sessions get an in-memory cooldown so we don't spin on re-accepts.
//
// Session state stores transport metadata (blocked status) as a versioned JSON envelope.
// The identity-matching mechanism: when a session is blocked, the pump peeks the
// next message and only processes it if the message's MessageId matches the one stored
// in session state. That's the "unblock" message. Once it succeeds, the session state
// is cleared and the rest of the session's messages flow normally.
internal class Program
{
    static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString")!;

    // Keeps blocked sessions out of the accept loop for a bit.
    // Without this, AcceptNextSessionAsync immediately returns the same blocked
    // session because it still has unconsumed messages.
    static readonly ConcurrentDictionary<string, DateTime> BlockedSessionCooldown = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan BlockedCooldownDuration = TimeSpan.FromSeconds(10);

    // Limits how many concurrent session processing slots are in use.
    // Workers that can't acquire a permit go back to accepting — keeps us from
    // overcommitting while still having enough accept goroutines to stay busy.
    static readonly ConcurrencyLimiter SessionConcurrency =
        new(new ConcurrencyLimiterOptions { PermitLimit = 3, QueueLimit = 0 });

    // Throttles how fast we attempt AcceptNextSessionAsync.
    // TokenBucket gives a steady cadence: burst up to 3 accepts immediately,
    // then one every 200ms. QueueLimit is unbounded so workers always wait
    // for a token instead of being rejected.
    static readonly TokenBucketRateLimiter AcceptThrottle =
        new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 3,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(200),
            QueueLimit = int.MaxValue,
            AutoReplenishment = true
        });

    static async Task Main(string[] args)
    {
        await using var cleanup = await Prepare.Stage(ConnectionString);

        await using var client = new ServiceBusClient(ConnectionString, new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpWebSockets,
            RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(30) }
        });

        // Bridge forwarders — one per subscription, both writing to the same input queue
        var bridgeCts = new CancellationTokenSource();
        var salesBridgeTask = RunSubscriptionBridgeAsync(Prepare.SalesTopicName, Prepare.SalesSub, "Sales", bridgeCts.Token);
        var inventoryBridgeTask = RunSubscriptionBridgeAsync(Prepare.InventoryTopicName, Prepare.InventorySub, "Inventory", bridgeCts.Token);

        // Input queue pump — consumes from the session-enabled input queue
        var pumpCts = new CancellationTokenSource();
        var pumpTask = RunInputQueuePumpAsync(client, pumpCts.Token);

        // DLQ retry processor — simulates what ServiceControl would do
        var dlqCts = new CancellationTokenSource();
        var dlqTask = RunDlqRetryProcessorAsync(client, dlqCts.Token);

        // Let the infrastructure settle before publishing
        await Task.Delay(2000);

        await PublishTestMessagesAsync(client);

        WriteLine("[MAIN] Waiting 60 seconds for processing...");
        await Task.Delay(TimeSpan.FromSeconds(60));

        pumpCts.Cancel();
        dlqCts.Cancel();
        bridgeCts.Cancel();

        try { await pumpTask; } catch (OperationCanceledException) { }
        try { await dlqTask; } catch (OperationCanceledException) { }
        try { await salesBridgeTask; } catch (OperationCanceledException) { }
        try { await inventoryBridgeTask; } catch (OperationCanceledException) { }

        WriteLine();
        WriteLine("=== Spike complete ===");
    }

    // ---------------------------------------------------------------
    // SUBSCRIPTION BRIDGE
    // ---------------------------------------------------------------

    // Boring session processor that copies messages from one session-enabled
    // subscription into the session-enabled input queue.
    //
    // Two instances run: one for sales-sub, one for inventory-sub. Both write
    // to the same input queue. This validates that multiple ordered subscriptions
    // can feed into one shared input queue without cross-subscription ordering
    // guarantees — each subscription preserves its own per-session order.
    //
    // Uses cross-entity transactions (EnableCrossEntityTransactions + TransactionScope)
    // for atomic receive-send-complete. Same pattern as the SendVia example in this repo.
    //
    // If the send fails, the subscription message stays locked and gets redelivered.
    // If the connection drops after send but before the complete ack, the input queue
    // gets a duplicate. That's acceptable for a bridge if handlers are idempotent,
    // which they should be anyway.
    static async Task RunSubscriptionBridgeAsync(string topicName, string subscriptionName, string label, CancellationToken ct)
    {
        var bridgeClient = new ServiceBusClient(ConnectionString, new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpWebSockets,
            RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(30) },
            EnableCrossEntityTransactions = true
        });

        await using var _ = bridgeClient;
        await using var inputQueueSender = bridgeClient.CreateSender(Prepare.InputQueueName);

        var bridgeProcessor = bridgeClient.CreateSessionProcessor(
            topicName,
            subscriptionName,
            new ServiceBusSessionProcessorOptions
            {
                AutoCompleteMessages = false,  // We manage completion inside the TransactionScope
                MaxConcurrentSessions = 3,
                MaxConcurrentCallsPerSession = 1,
                SessionIdleTimeout = TimeSpan.FromSeconds(3),
                PrefetchCount = 100,  // Bottleneck-free forwarder, no reason to limit this
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        bridgeProcessor.ProcessMessageAsync += async args =>
        {
            var message = args.Message;
            var sessionId = args.SessionId;

            WriteLine($"[BRIDGE-{label}] Received '{message.MessageId}' on session '{sessionId}'");

            // Wrap send + complete in a TransactionScope. With EnableCrossEntityTransactions,
            // both go through the same AMQP link and commit atomically.
            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                // The SDK's built-in copy constructor copies body, SessionId (GroupId),
                // app properties, annotations, headers. DeliveryCount is set to null
                // (fresh message). Broker annotations (seq#, enqueued-time, locked-until)
                // are excluded — so the copy gets fresh values from the input queue.
                var forwarded = new ServiceBusMessage(message);

                // Setting SessionId also syncs PartitionKey
                forwarded.SessionId = sessionId;

                await inputQueueSender.SendMessageAsync(forwarded, ct);

                WriteLine($"[BRIDGE-{label}] Sent to input queue, completing source...");

                await args.CompleteMessageAsync(message, ct);

                scope.Complete();
            }

            WriteLine($"[BRIDGE-{label}] Forwarded '{message.MessageId}' (session '{sessionId}')");
        };

        bridgeProcessor.ProcessErrorAsync += args =>
        {
            WriteLine($"[BRIDGE-{label}] Error: {args.Exception.Message}");
            if (args.Exception.InnerException != null)
                WriteLine($"[BRIDGE-{label}] Inner: {args.Exception.InnerException.Message}");
            return Task.CompletedTask;
        };

        await bridgeProcessor.StartProcessingAsync(ct);
        WriteLine($"[BRIDGE-{label}] Started (sub: {subscriptionName})");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            WriteLine($"[BRIDGE-{label}] Shutting down...");
            await bridgeProcessor.StopProcessingAsync();
        }
    }

    // ---------------------------------------------------------------
    // INPUT QUEUE SESSION PUMP
    // ---------------------------------------------------------------

    // Spawns a pool of accept workers. Each worker loops:
    //   1. Throttle: wait for the TokenBucket to allow an accept attempt
    //   2. Acquire: call AcceptNextSessionAsync
    //   3. Permit: acquire a ConcurrencyLimiter slot before processing
    //   4. Process: hand off to ProcessSessionAsync, then release permit
    //
    // Using two rate limiters:
    //   - TokenBucketRateLimiter — smooths the accept cadence (~1 per 200ms)
    //   - ConcurrencyLimiter — caps how many sessions are held simultaneously
    //
    // This is cleaner than ad-hoc Task.Delay calls and manual task tracking.
    static async Task RunInputQueuePumpAsync(ServiceBusClient client, CancellationToken ct)
    {
        const int AcceptWorkers = 5;
        var stats = SessionConcurrency.GetStatistics();
        var availablePermits = stats?.CurrentAvailablePermits ?? 0;
        WriteLine($"[PUMP] Starting ({AcceptWorkers} accept workers, concurrency limited to {availablePermits}, manual AcceptNextSessionAsync)...");

        var workers = new Task[AcceptWorkers];
        for (int i = 0; i < AcceptWorkers; i++)
        {
            var workerId = i + 1;
            workers[i] = RunPumpWorkerAsync(client, workerId, ct);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        WriteLine("[PUMP] Stopped");
    }

    // Each worker loops:
    //   1. Acquire concurrency permit (cap how many sessions process at once)
    //   2. Throttle accept rate (TokenBucket, ~1 per 200ms)
    //   3. Accept a session
    //   4. Check in-memory cooldown — skip blocked sessions
    //   5. Process the session
    //   6. Release the permit (finally)
    //
    // Permit-first: we never accept a session we can't immediately process.
    // The 3-permit limit means we hold at most 3 session locks at any time.
    static async Task RunPumpWorkerAsync(ServiceBusClient client, int workerId, CancellationToken ct)
    {
        WriteLine($"[PUMP-{workerId}] Started");

        while (!ct.IsCancellationRequested)
        {
            ServiceBusSessionReceiver? sessionReceiver = null;
            RateLimitLease? concurrencyLease = null;

            try
            {
                // Step 1: acquire a concurrency permit FIRST.
                // This represents "I have capacity to process one session right now."
                // If all permits are taken, we wait until one frees up.
                concurrencyLease = await SessionConcurrency.AcquireAsync(
                    permitCount: 1, ct);

                if (!concurrencyLease.IsAcquired)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                // Step 2: throttle the accept rate so we don't hammer the broker
                using var throttleLease = await AcceptThrottle.AcquireAsync(
                    permitCount: 1, ct);

                if (!throttleLease.IsAcquired)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                // Step 3: accept a session
                sessionReceiver = await client.AcceptNextSessionAsync(
                    Prepare.InputQueueName,
                    new ServiceBusSessionReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.PeekLock,
                        PrefetchCount = 0  // Precise control — no prefetch
                    },
                    ct);

                var sessionId = sessionReceiver.SessionId;
                WriteLine($"[PUMP-{workerId}] Accepted session '{sessionId}'");

                // In-memory cooldown check before reading session state.
                if (IsSessionInCooldown(sessionId))
                {
                    WriteLine($"[PUMP-{workerId}] Session '{sessionId}' is in cooldown — skipping");
                    await ReleaseSessionAsync(sessionReceiver, sessionId);
                    continue;
                }

                // Steps 4–5: process, then release the permit in finally
                await ProcessSessionAsync(sessionReceiver, sessionId, workerId, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ServiceBusException ex)
                when (ex.Reason == ServiceBusFailureReason.ServiceTimeout
                   || ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                if (sessionReceiver != null)
                    await sessionReceiver.DisposeAsync();
            }
            catch (Exception ex)
            {
                WriteLine($"[PUMP-{workerId}] Error: {ex.Message}");
                if (sessionReceiver != null)
                    await sessionReceiver.DisposeAsync();
            }
            finally
            {
                concurrencyLease?.Dispose();
            }
        }

        WriteLine($"[PUMP-{workerId}] Stopped");
    }

    // Processes a single session. When a session is blocked, we peek the next message.
    // Only the message whose MessageId matches the stored BlockedMessageId is the
    // "unblock" message. Everything else ahead of it gets deferred (we release and
    // let the cooldown handle the retry).
    //
    // Once the unblock message succeeds, session state is cleared and remaining
    // messages in the session flow normally on subsequent pump iterations.
    //
    // availablePermits caps how many messages we pull at once — no point fetching
    // this session, one session at a time.
    static async Task ProcessSessionAsync(ServiceBusSessionReceiver receiver, string sessionId, int workerId, CancellationToken ct)
    {
        var sessionState = await ReadSessionStateAsync(receiver, ct);

        if (sessionState.IsBlocked)
        {
            WriteLine($"[PUMP-{workerId}] Session '{sessionId}' is BLOCKED (by msg '{sessionState.BlockedMessageId}'). Checking for unblock message...");

            var nextMsg = await receiver.PeekMessageAsync(cancellationToken: ct);

            if (nextMsg == null)
            {
                WriteLine($"[PUMP-{workerId}]   No messages in blocked session — releasing.");
                AddBlockedCooldown(sessionId, workerId);
                await ReleaseSessionAsync(receiver, sessionId);
                return;
            }

            if (string.Equals(nextMsg.MessageId, sessionState.BlockedMessageId, StringComparison.Ordinal))
            {
                WriteLine($"[PUMP-{workerId}]   Found unblock message '{nextMsg.MessageId}' — will process it.");
                // Fall through — ReceiveMessagesAsync will consume it and we check again
            }
            else
            {
                WriteLine($"[PUMP-{workerId}]   Next msg is '{nextMsg.MessageId}' — doesn't match blocked '{sessionState.BlockedMessageId}'. Releasing.");
                AddBlockedCooldown(sessionId, workerId);
                await ReleaseSessionAsync(receiver, sessionId);
                return;
            }
        }

        WriteLine($"[PUMP-{workerId}] Session '{sessionId}' is clear. Receiving messages...");

        var messages = await receiver.ReceiveMessagesAsync(
            maxMessages: 10,
            maxWaitTime: TimeSpan.FromSeconds(5),
            ct);

        if (messages.Count == 0)
        {
            WriteLine($"[PUMP-{workerId}] Session '{sessionId}' has no messages. Releasing.");
            await ReleaseSessionAsync(receiver, sessionId);
            return;
        }

        foreach (var message in messages)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var body = Encoding.UTF8.GetString(message.Body);
                WriteLine($"[PUMP-{workerId}]   Processing '{message.MessageId}' (session '{sessionId}'): {body}");

                if (message.ApplicationProperties.TryGetValue("SimulateFailure", out var flag)
                    && flag is bool shouldFail && shouldFail)
                {
                    throw new InvalidOperationException($"Simulated failure for '{message.MessageId}'");
                }

                await receiver.CompleteMessageAsync(message, ct);
                WriteLine($"[PUMP-{workerId}]   Completed '{message.MessageId}'");

                // If this was the unblock message, clear session state so remaining
                // messages in the session can be picked up next iteration.
                if (sessionState.IsBlocked && string.Equals(message.MessageId, sessionState.BlockedMessageId, StringComparison.Ordinal))
                {
                    await ClearSessionBlockedStateAsync(receiver, sessionId, workerId);
                    WriteLine($"[PUMP-{workerId}]   Unblock message completed — session '{sessionId}' is now UNBLOCKED");
                    BlockedSessionCooldown.TryRemove(sessionId, out _);
                }
            }
            catch (Exception ex)
            {
                WriteLine($"[PUMP-{workerId}]   FAILED '{message.MessageId}': {ex.Message}");

                await MarkSessionBlockedAsync(receiver, sessionId, message.MessageId, workerId);

                await receiver.DeadLetterMessageAsync(
                    message,
                    deadLetterReason: "ProcessingFailure",
                    deadLetterErrorDescription: ex.Message,
                    cancellationToken: ct);

                WriteLine($"[PUMP-{workerId}]   Dead-lettered '{message.MessageId}', session '{sessionId}' = BLOCKED");

                // Remaining messages in this session stay in the queue.
                // The cooldown prevents tight re-accept loops.
                break;
            }
        }

        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // ---------------------------------------------------------------
    // SESSION STATE HELPERS
    // ---------------------------------------------------------------

    // Reads the transport-managed session state from ASB. Returns default
    // (unblocked) if nothing is stored or parsing fails.
    static async Task<SessionState> ReadSessionStateAsync(ServiceBusSessionReceiver receiver, CancellationToken ct)
    {
        try
        {
            var binaryState = await receiver.GetSessionStateAsync(ct);
            if (binaryState == null) return SessionState.Default;

            var json = Encoding.UTF8.GetString(binaryState);
            return SessionState.FromJson(json);
        }
        catch (Exception ex)
        {
            WriteLine($"[PUMP] Warning reading session state: {ex.Message}");
            return SessionState.Default;
        }
    }

    // Stores the blocked marker + the failed message's MessageId in ASB session state.
    static async Task MarkSessionBlockedAsync(ServiceBusSessionReceiver receiver, string sessionId, string failedMessageId, int workerId)
    {
        var state = new SessionState
        {
            IsBlocked = true,
            BlockedMessageId = failedMessageId,
            BlockedAt = DateTimeOffset.UtcNow
        };

        var binaryData = BinaryData.FromBytes(Encoding.UTF8.GetBytes(state.ToJson()));
        await receiver.SetSessionStateAsync(binaryData);
        WriteLine($"[PUMP-{workerId}] Session state: '{sessionId}' = BLOCKED (msg: {failedMessageId})");
    }

    // Clears ASB session state entirely — null means no blocking metadata.
    static async Task ClearSessionBlockedStateAsync(ServiceBusSessionReceiver receiver, string sessionId, int workerId)
    {
        await receiver.SetSessionStateAsync(null as BinaryData);
        WriteLine($"[PUMP-{workerId}] Session state: '{sessionId}' = UNBLOCKED");
    }

    // Releases a session receiver by closing it. Subsequent pump iterations
    // can then re-accept the session.
    static async Task ReleaseSessionAsync(ServiceBusSessionReceiver receiver, string sessionId, int? workerId = null)
    {
        var tag = workerId.HasValue ? $"[PUMP-{workerId}]" : "[PUMP]";
        try
        {
            await receiver.CloseAsync();
            WriteLine($"{tag} Released session '{sessionId}'");
        }
        catch (Exception ex)
        {
            WriteLine($"{tag} Warning releasing '{sessionId}': {ex.Message}");
        }
    }

    // Adds the session to the in-memory cooldown map. The pump will skip it
    // until the cooldown expires. Gives the DLQ retry processor time to operate
    // without us spinning on re-accepts.
    static void AddBlockedCooldown(string sessionId, int? workerId = null)
    {
        BlockedSessionCooldown[sessionId] = DateTime.UtcNow + BlockedCooldownDuration;
        var tag = workerId.HasValue ? $"[PUMP-{workerId}]" : "[PUMP]";
        WriteLine($"{tag} Cooldown set for '{sessionId}' ({BlockedCooldownDuration.TotalSeconds}s)");
    }

    static bool IsSessionInCooldown(string sessionId)
    {
        if (BlockedSessionCooldown.TryGetValue(sessionId, out var cooldownUntil))
        {
            if (DateTime.UtcNow < cooldownUntil)
                return true;

            // Cooldown expired — clean up and allow
            BlockedSessionCooldown.TryRemove(sessionId, out _);
        }
        return false;
    }

    // ---------------------------------------------------------------
    // DLQ RETRY PROCESSOR
    // ---------------------------------------------------------------

    // Reads from the dead-letter queue (sales-input/$DeadLetterQueue) and re-sends
    // messages back to the input queue. Simulates what ServiceControl would do in
    // a production setup.
    //
    // On retry, clears the failed session's blocked state so the pump can process
    // the retried message (and any backlogged messages in that session). The pump's
    // identity-matching logic then takes over — only the retried message (matching
    // by MessageId) unblocks the session.
    static async Task RunDlqRetryProcessorAsync(ServiceBusClient client, CancellationToken ct)
    {
        // Give the pump time to dead-letter before we scan the DLQ
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }

        WriteLine("[DLQ] Starting DLQ retry processor...");

        var dlqProcessor = client.CreateProcessor(
            Prepare.InputQueueName,
            new ServiceBusProcessorOptions
            {
                SubQueue = SubQueue.DeadLetter,
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 3,
                PrefetchCount = 0,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        dlqProcessor.ProcessMessageAsync += async args =>
        {
            var message = args.Message;
            WriteLine($"[DLQ] Found dead-lettered message '{message.MessageId}'");
            WriteLine($"[DLQ]   DeadLetterReason: {message.DeadLetterReason}");
            WriteLine($"[DLQ]   DeadLetterErrorDescription: {message.DeadLetterErrorDescription}");

            await using var sender = client.CreateSender(Prepare.InputQueueName);

            // Create a copy and strip the failure flag and DLQ annotations
            var retryMessage = new ServiceBusMessage(message);
            retryMessage.ApplicationProperties.Remove("SimulateFailure");

            // Track retry count to avoid infinite DLQ loops
            var retryCount = 0;
            if (message.ApplicationProperties.TryGetValue("RetryCount", out var rc) && rc is int existingCount)
            {
                retryCount = existingCount;
            }
            retryCount++;
            retryMessage.ApplicationProperties["RetryCount"] = retryCount;

            if (retryCount > 3)
            {
                WriteLine($"[DLQ] Message '{message.MessageId}' exceeded max retries ({retryCount}). Dead-lettering permanently.");
                await args.DeadLetterMessageAsync(message, "MaxRetriesExceeded", "Exceeded maximum retry count", ct);
                return;
            }

            WriteLine($"[DLQ] Retry #{retryCount} for '{message.MessageId}'");

            // The copy constructor copies GroupId, but setting SessionId explicitly
            // also syncs PartitionKey — needed for partitioned entities.
            retryMessage.SessionId = message.SessionId;

            await sender.SendMessageAsync(retryMessage, ct);
            WriteLine($"[DLQ] Re-sent '{message.MessageId}' to input queue (session '{message.SessionId}')");

            await args.CompleteMessageAsync(message, ct);
            WriteLine($"[DLQ] Completed DLQ message '{message.MessageId}'");

            // Clear the session state so the pump can accept the session again.
            // The pump's identity-matching will let only the retried message through.
            if (!string.IsNullOrEmpty(message.SessionId))
            {
                await ClearSessionBlockedStateForSessionAsync(client, message.SessionId, ct);
                BlockedSessionCooldown.TryRemove(message.SessionId, out _);
                WriteLine($"[DLQ] Cleared blocked state + cooldown for session '{message.SessionId}'");
            }
        };

        dlqProcessor.ProcessErrorAsync += args =>
        {
            WriteLine($"[DLQ] Error: {args.Exception.Message}");
            return Task.CompletedTask;
        };

        await dlqProcessor.StartProcessingAsync(ct);
        WriteLine("[DLQ] Started, waiting for DLQ messages...");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            WriteLine("[DLQ] Shutting down...");
            await dlqProcessor.StopProcessingAsync();
        }
    }

    // Opens a temporary session receiver to clear the blocked session state.
    // This is a simulation shortcut — a real ServiceControl retry would preserve
    // headers that the pump correlates to decide which message unblocks the session.
    static async Task ClearSessionBlockedStateForSessionAsync(ServiceBusClient client, string sessionId, CancellationToken ct)
    {
        try
        {
            var receiver = await client.AcceptSessionAsync(
                Prepare.InputQueueName,
                sessionId,
                new ServiceBusSessionReceiverOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                    PrefetchCount = 0
                },
                ct);

            await using var _ = receiver;

            await receiver.SetSessionStateAsync(null as BinaryData, ct);
            WriteLine($"[DLQ] Cleared session state for '{sessionId}'");

            await receiver.CloseAsync();
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // Session might be locked by the pump — that's fine. The pump will
            // eventually check session state after the cooldown expires.
            WriteLine($"[DLQ] Could not lock session '{sessionId}' for state clear (being processed): {ex.Message}");
        }
        catch (Exception ex)
        {
            WriteLine($"[DLQ] Warning clearing session state for '{sessionId}': {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // TEST MESSAGES
    // ---------------------------------------------------------------

    static async Task PublishTestMessagesAsync(ServiceBusClient client)
    {
        await using var salesSender = client.CreateSender(Prepare.SalesTopicName);
        await using var inventorySender = client.CreateSender(Prepare.InventoryTopicName);

        WriteLine();
        WriteLine("=== Publishing sales messages ===");

        // Session "Customer-123": msg1 succeeds, msg2 fails (blocks the session), msg3 stuck
        var m1 = new ServiceBusMessage("Order received for Customer-123")
        { MessageId = "cust123-msg1", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m1);
        WriteLine($"  Published '{m1.MessageId}' (session: Customer-123)");

        var m2 = new ServiceBusMessage("Payment processing for Customer-123")
        { MessageId = "cust123-msg2", SessionId = "Customer-123" };
        m2.ApplicationProperties["SimulateFailure"] = true;
        await salesSender.SendMessageAsync(m2);
        WriteLine($"  Published '{m2.MessageId}' (session: Customer-123) [WILL FAIL]");

        var m3 = new ServiceBusMessage("Shipping for Customer-123")
        { MessageId = "cust123-msg3", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m3);
        WriteLine($"  Published '{m3.MessageId}' (session: Customer-123)");

        // Session "Customer-456": all succeed — happy path
        var m4 = new ServiceBusMessage("Order received for Customer-456")
        { MessageId = "cust456-msg1", SessionId = "Customer-456" };
        await salesSender.SendMessageAsync(m4);
        WriteLine($"  Published '{m4.MessageId}' (session: Customer-456)");

        var m5 = new ServiceBusMessage("Payment processed for Customer-456")
        { MessageId = "cust456-msg2", SessionId = "Customer-456" };
        await salesSender.SendMessageAsync(m5);
        WriteLine($"  Published '{m5.MessageId}' (session: Customer-456)");

        // Session "Customer-789": fails immediately — exercises the DLQ retry path
        var m6 = new ServiceBusMessage("Order for Customer-789")
        { MessageId = "cust789-msg1", SessionId = "Customer-789" };
        m6.ApplicationProperties["SimulateFailure"] = true;
        await salesSender.SendMessageAsync(m6);
        WriteLine($"  Published '{m6.MessageId}' (session: Customer-789) [WILL FAIL]");

        WriteLine();
        WriteLine("=== Publishing inventory messages ===");

        // Inventory session "Stock-001": from a different topic + subscription.
        // The inventory bridge forwards them into the same input queue.
        var i1 = new ServiceBusMessage("Stock level check for SKU-001")
        { MessageId = "stock001-msg1", SessionId = "Stock-001" };
        await inventorySender.SendMessageAsync(i1);
        WriteLine($"  Published '{i1.MessageId}' (session: Stock-001)");

        var i2 = new ServiceBusMessage("Stock reservation for SKU-001")
        { MessageId = "stock001-msg2", SessionId = "Stock-001" };
        await inventorySender.SendMessageAsync(i2);
        WriteLine($"  Published '{i2.MessageId}' (session: Stock-001)");

        // Inventory session "Stock-002": another independent session.
        var i3 = new ServiceBusMessage("Stock level check for SKU-002")
        { MessageId = "stock002-msg1", SessionId = "Stock-002" };
        await inventorySender.SendMessageAsync(i3);
        WriteLine($"  Published '{i3.MessageId}' (session: Stock-002)");

        WriteLine("=== Publishing complete ===");
        WriteLine();
    }
}

// ---------------------------------------------------------------
// SESSION STATE (versioned JSON envelope)
// ---------------------------------------------------------------

// Transport-managed session state stored in ASB session state.
// The transport owns the "transport" section; user code owns a separate "user" section.
// This record only models the transport side for the spike.
public record SessionState
{
    public bool IsBlocked { get; init; }
    public string? BlockedMessageId { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }

    public static SessionState Default => new() { IsBlocked = false };

    public string ToJson()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            transport = new
            {
                blocked = IsBlocked,
                blockedMessageId = BlockedMessageId,
                blockedAt = BlockedAt?.ToString("O")
            }
        });
    }

    public static SessionState FromJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var t = doc.RootElement.GetProperty("transport");
            if (!t.GetProperty("blocked").GetBoolean())
                return Default;

            return new SessionState
            {
                IsBlocked = true,
                BlockedMessageId = t.GetProperty("blockedMessageId").GetString(),
                BlockedAt = t.TryGetProperty("blockedAt", out var ba) && ba.ValueKind == System.Text.Json.JsonValueKind.String
                    ? DateTimeOffset.Parse(ba.GetString()!)
                    : null
            };
        }
        catch
        {
            return Default;
        }
    }
}