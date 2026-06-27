#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using static System.Console;

namespace SubscriptionBridge;

// Spike: session-enabled subscription bridge + input queue session pump with
// scheduled-resend delayed retries.
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
// Recoverability model — SCHEDULED RESEND + peek-and-search hold-back:
//
// When a message fails, the pump:
//   1. Marks the session BLOCKED in ASB session state (BlockedMessageId = the
//      failed message's MessageId, RetryAfter = now + delay).
//   2. Schedules a fresh copy with the SAME SessionId + MessageId and
//      ScheduledEnqueueTime = now + delay.
//   3. Completes the original (it's been copied; the scheduled copy IS the retry).
//   4. Releases the session.
//
// The hold-back (peek-and-search):
//   - When a blocked session is accepted, the pump peeks a window and searches for
//     BlockedMessageId. If not found (retry scheduled but not yet visible), it
//     cooldowns until RetryAfter and releases.
//   - When found, it receives up to and including the match, ABANDONS the backlog
//     prefix (hold-back), processes ONLY the match, and clears the block on success.
//   - After unblock, the abandoned backlog re-flows in original order.
//
// Why scheduled resend over defer:
//   - Defer strands messages on process restart (deferred messages don't expire to
//     DLQ; they're only recoverable by peek-scan). Scheduled messages are normal
//     broker state — no registry, no recovery scan, no orphan risk.
//   - The peek-and-search hold-back is needed anyway for ServiceControl retries
//     (which re-send as normal messages behind the backlog), so it's not extra work.
//   - Trade-off: scheduled copies get fresh sequence numbers, fresh enqueue times,
//     and reset DeliveryCount. Retry count is carried as an application property.
internal class Program
{
    static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString")!;

    // Keeps blocked sessions out of the accept loop while their retry isn't due yet.
    // Derived from RetryAfter (not a flat duration) so we don't spin or overshoot.
    static readonly ConcurrentDictionary<string, DateTime> BlockedSessionCooldown = new(StringComparer.OrdinalIgnoreCase);

    static readonly ConcurrencyLimiter SessionConcurrency =
        new(new ConcurrencyLimiterOptions { PermitLimit = 3, QueueLimit = 0 });

    static readonly TokenBucketRateLimiter AcceptThrottle =
        new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 3,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(200),
            QueueLimit = int.MaxValue,
            AutoReplenishment = true
        });

    // --- Delayed-retry configuration (scheduled resend) ---
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(6);
    const int MaxAttempts = 5;

    // Per-MessageId simulated failure budget: fail on the first N handling attempts,
    // then succeed. The scheduled copy carries the same MessageId, so the budget
    // naturally spans original + retries.
    static readonly ConcurrentDictionary<string, int> FailureAttempts = new();
    static readonly Dictionary<string, int> FailureBudget = new()
    {
        ["cust123-msg2"] = 1,   // fails attempt 1, succeeds on the scheduled retry
        ["cust789-msg1"] = 2,   // fails attempts 1 & 2, succeeds on the second scheduled retry
    };

    // args: "restart" => run the restart-durability scenario; otherwise the normal
    // 60s run. The restart scenario publishes, waits for msg2 to fail + schedule its
    // retry, then tears down the pump + client (simulating a process stop), waits out
    // the retry delay with NO pump running, and recreates a fresh pump on a new client.
    // The falsifiable claim: the scheduled message (broker state) + blocked marker
    // (session state) both survive, and the fresh pump's hold-back reconstructs from
    // durable state — order still holds, no message orphaned. This is a faithful
    // simulation of a process restart because the only in-memory state (the cooldown
    // map) is derived from RetryAfter in session state.
    static async Task Main(string[] args)
    {
        var restartMode = args.Length > 0 && string.Equals(args[0], "restart", StringComparison.OrdinalIgnoreCase);
        await using var cleanup = await Prepare.Stage(ConnectionString);

        if (restartMode)
        {
            await RunRestartScenarioAsync();
        }
        else
        {
            await RunNormalScenarioAsync();
        }

        WriteLine();
        WriteLine("=== Spike complete ===");
    }

    static async Task RunNormalScenarioAsync()
    {
        await using var client = NewClient();

        var bridgeCts = new CancellationTokenSource();
        var salesBridgeTask = RunSubscriptionBridgeAsync(Prepare.SalesTopicName, Prepare.SalesSub, "Sales", bridgeCts.Token);
        var inventoryBridgeTask = RunSubscriptionBridgeAsync(Prepare.InventoryTopicName, Prepare.InventorySub, "Inventory", bridgeCts.Token);

        var pumpCts = new CancellationTokenSource();
        var pumpTask = RunInputQueuePumpAsync(client, pumpCts.Token);

        var dlqCts = new CancellationTokenSource();
        var dlqTask = RunDlqRetryProcessorAsync(client, dlqCts.Token);

        await Task.Delay(2000);

        await PublishTestMessagesAsync(client);

        // Concurrent producer: keeps feeding Customer-123 WHILE msg2's retry is
        // scheduled. The hold-back must prevent ALL of these (and msg3) from
        // completing before msg2's scheduled retry succeeds.
        var concurrentPubTask = RunConcurrentPublisherAsync(client, bridgeCts.Token);

        WriteLine("[MAIN] Waiting 60 seconds for processing...");
        await Task.Delay(TimeSpan.FromSeconds(60));

        pumpCts.Cancel();
        dlqCts.Cancel();
        bridgeCts.Cancel();

        try { await pumpTask; } catch (OperationCanceledException) { }
        try { await dlqTask; } catch (OperationCanceledException) { }
        try { await salesBridgeTask; } catch (OperationCanceledException) { }
        try { await inventoryBridgeTask; } catch (OperationCanceledException) { }
        try { await concurrentPubTask; } catch (OperationCanceledException) { }
    }

    // Restart-durability scenario. Phase 1 establishes a blocked session with a
    // scheduled retry in flight, then everything stops. Phase 2 starts a brand-new
    // pump on a brand-new client after the retry delay has elapsed, and must recover.
    static async Task RunRestartScenarioAsync()
    {
        // ---- Phase 1: bring up infra, publish, let msg2 fail + schedule its retry ----
        WriteLine("[RESTART] === Phase 1: start, publish, establish block ===");
        var client1 = NewClient();

        var bridgeCts = new CancellationTokenSource();
        var salesBridgeTask1 = RunSubscriptionBridgeAsync(Prepare.SalesTopicName, Prepare.SalesSub, "Sales", bridgeCts.Token);
        var inventoryBridgeTask1 = RunSubscriptionBridgeAsync(Prepare.InventoryTopicName, Prepare.InventorySub, "Inventory", bridgeCts.Token);

        var pumpCts1 = new CancellationTokenSource();
        var pumpTask1 = RunInputQueuePumpAsync(client1, pumpCts1.Token);

        await Task.Delay(2000);
        await PublishTestMessagesAsync(client1);

        // Wait long enough for cust123-msg1 to complete, cust123-msg2 to FAIL, the
        // session to be marked blocked, and the scheduled retry (+6s) to be in flight.
        // 4s after publish: msg2 has failed, block is set, retry scheduled, pump released
        // the session. cust789 likewise. Concurrent publisher not started here — keep the
        // backlog fixed for a deterministic recovery assertion.
        WriteLine("[RESTART] Letting failures establish + scheduling retries...");
        await Task.Delay(TimeSpan.FromSeconds(4));

        WriteLine("[RESTART] === STOP: tearing down pump + client (simulating process stop) ===");
        pumpCts1.Cancel();
        bridgeCts.Cancel();
        try { await pumpTask1; } catch (OperationCanceledException) { }
        try { await salesBridgeTask1; } catch (OperationCanceledException) { }
        try { await inventoryBridgeTask1; } catch (OperationCanceledException) { }
        await client1.DisposeAsync();

        // In-memory cooldown is now GONE. Broker still holds: the scheduled retry
        // messages (normal broker state), the blocked session markers (ASB session
        // state), and the backlogs (msg3 etc.). Wait past the retry delay with NO pump.
        WriteLine("[RESTART] === DOWN: no pump running, waiting out retry delay ===");
        await Task.Delay(TimeSpan.FromSeconds(10));

        // ---- Phase 2: fresh client, fresh pump, empty cooldown. Must recover. ----
        WriteLine("[RESTART] === START: fresh pump on new client — must recover from durable state ===");
        await using var client2 = NewClient();
        var pumpCts2 = new CancellationTokenSource();
        var pumpTask2 = RunInputQueuePumpAsync(client2, pumpCts2.Token);

        // Give recovery time to run: recall scheduled retries, clear blocks, drain backlog.
        WriteLine("[RESTART] Waiting 30 seconds for recovery...");
        await Task.Delay(TimeSpan.FromSeconds(30));

        pumpCts2.Cancel();
        try { await pumpTask2; } catch (OperationCanceledException) { }
    }

    static ServiceBusClient NewClient() => new(ConnectionString, new ServiceBusClientOptions
    {
        TransportType = ServiceBusTransportType.AmqpWebSockets,
        RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(30) }
    });

    // ---------------------------------------------------------------
    // SUBSCRIPTION BRIDGE
    // ---------------------------------------------------------------

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
                AutoCompleteMessages = false,
                MaxConcurrentSessions = 3,
                MaxConcurrentCallsPerSession = 1,
                SessionIdleTimeout = TimeSpan.FromSeconds(3),
                PrefetchCount = 100,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        bridgeProcessor.ProcessMessageAsync += async args =>
        {
            var message = args.Message;
            var sessionId = args.SessionId;

            WriteLine($"[BRIDGE-{label}] Received '{message.MessageId}' on session '{sessionId}'");

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var forwarded = new ServiceBusMessage(message);
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

    static async Task RunInputQueuePumpAsync(ServiceBusClient client, CancellationToken ct)
    {
        const int AcceptWorkers = 5;
        WriteLine($"[PUMP] Starting ({AcceptWorkers} accept workers, concurrency limited to 3, manual AcceptNextSessionAsync)...");

        // One sender for scheduled resends — reused across all workers.
        await using var inputQueueSender = client.CreateSender(Prepare.InputQueueName);

        var workers = new Task[AcceptWorkers];
        for (int i = 0; i < AcceptWorkers; i++)
        {
            var workerId = i + 1;
            workers[i] = RunPumpWorkerAsync(client, inputQueueSender, workerId, ct);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
        }

        WriteLine("[PUMP] Stopped");
    }

    static async Task RunPumpWorkerAsync(ServiceBusClient client, ServiceBusSender inputQueueSender, int workerId, CancellationToken ct)
    {
        WriteLine($"[PUMP-{workerId}] Started");

        while (!ct.IsCancellationRequested)
        {
            ServiceBusSessionReceiver? sessionReceiver = null;
            RateLimitLease? concurrencyLease = null;

            try
            {
                concurrencyLease = await SessionConcurrency.AcquireAsync(1, ct);
                if (!concurrencyLease.IsAcquired)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                using var throttleLease = await AcceptThrottle.AcquireAsync(1, ct);
                if (!throttleLease.IsAcquired)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                sessionReceiver = await client.AcceptNextSessionAsync(
                    Prepare.InputQueueName,
                    new ServiceBusSessionReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.PeekLock,
                        PrefetchCount = 0
                    },
                    ct);

                var sessionId = sessionReceiver.SessionId;

                // Cooldown check: skip blocked sessions whose retry isn't due yet.
                // Prevents hot-spinning on blocked sessions during the retry delay.
                if (IsSessionInCooldown(sessionId))
                {
                    await ReleaseSessionAsync(sessionReceiver, sessionId);
                    continue;
                }

                await ProcessSessionAsync(sessionReceiver, inputQueueSender, sessionId, workerId, ct);
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

    // Processes a single session. Two paths:
    //
    // BLOCKED: The session is pinned to BlockedMessageId by a prior failure. The pump
    // peeks for that MessageId. If the scheduled retry hasn't arrived yet, cooldown +
    // release. If it has arrived, receive up to it, abandon the backlog prefix (hold-
    // back), process ONLY the match, clear block on success. The abandoned backlog
    // re-flows on the next accept in original order.
    //
    // CLEAR: Normal FIFO receive + process.
    static async Task ProcessSessionAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, string sessionId, int workerId, CancellationToken ct)
    {
        var sessionState = await ReadSessionStateAsync(receiver, ct);

        if (sessionState.IsBlocked)
        {
            await ProcessBlockedSessionAsync(receiver, inputQueueSender, sessionId, sessionState, workerId, ct);
            return;
        }

        WriteLine($"[PUMP-{workerId}] Session '{sessionId}' is clear. Receiving messages...");

        var messages = await receiver.ReceiveMessagesAsync(
            maxMessages: 10,
            maxWaitTime: TimeSpan.FromSeconds(5),
            ct);

        if (messages.Count == 0)
        {
            WriteLine($"[PUMP-{workerId}] Session '{sessionId}' has no messages. Releasing.");
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        foreach (var message in messages)
        {
            if (ct.IsCancellationRequested) break;
            var ok = await TryHandleAsync(receiver, inputQueueSender, message, sessionId, workerId, ct);
            if (!ok) break; // session blocked by a failure; stop draining this batch
        }

        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // Hold-back logic for a blocked session. Peeks for BlockedMessageId:
    //   - Not found → retry not yet visible → cooldown until RetryAfter, release.
    //   - Found → receive batch, abandon prefix, process match, clear block.
    static async Task ProcessBlockedSessionAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, string sessionId, SessionState sessionState, int workerId, CancellationToken ct)
    {
        WriteLine($"[PUMP-{workerId}] Session '{sessionId}' BLOCKED on '{sessionState.BlockedMessageId}'. Peeking for scheduled retry...");

        // Peek a window. If the scheduled retry hasn't been enqueued yet (its
        // ScheduledEnqueueTime is in the future), it won't appear here.
        var peeked = await receiver.PeekMessagesAsync(maxMessages: 32, cancellationToken: ct);
        var matchSeq = peeked.FirstOrDefault(m =>
            string.Equals(m.MessageId, sessionState.BlockedMessageId, StringComparison.Ordinal))?.SequenceNumber;

        if (matchSeq == null)
        {
            // Retry not yet visible. Cooldown until RetryAfter so we don't spin.
            var cooldownUntil = sessionState.RetryAfter ?? DateTimeOffset.UtcNow.AddSeconds(1);
            if (cooldownUntil > DateTimeOffset.UtcNow)
            {
                BlockedSessionCooldown[sessionId] = cooldownUntil.UtcDateTime;
            }
            var wait = cooldownUntil - DateTimeOffset.UtcNow;
            WriteLine($"[PUMP-{workerId}]   Retry not yet visible — cooldown {(int)Math.Ceiling(wait.TotalSeconds)}s. Releasing.");
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        WriteLine($"[PUMP-{workerId}]   Found blocked message (seq {matchSeq}) — draining to it...");

        // Receive a batch. The match should be within it (we just peeked it). Messages
        // before the match are backlog that arrived while blocked — abandon them so they
        // re-flow after unblock. This is the hold-back: they are NOT processed.
        var messages = await receiver.ReceiveMessagesAsync(
            maxMessages: 32,
            maxWaitTime: TimeSpan.FromSeconds(2),
            ct);

        var blockCleared = false;

        foreach (var message in messages)
        {
            if (ct.IsCancellationRequested) break;

            if (!blockCleared && !string.Equals(message.MessageId, sessionState.BlockedMessageId, StringComparison.Ordinal))
            {
                // Backlog before the retry — hold back by abandoning. It becomes available
                // again and re-flows in original order once the block clears.
                await receiver.AbandonMessageAsync(message, cancellationToken: ct);
                WriteLine($"[PUMP-{workerId}]   Held back '{message.MessageId}' (ahead of blocked msg) — abandoned.");
                continue;
            }

            // This is the blocked message (or block already cleared). Process it.
            var ok = await TryHandleAsync(receiver, inputQueueSender, message, sessionId, workerId, ct);

            if (ok && !blockCleared)
            {
                // The blocked message succeeded — clear the block.
                await ClearSessionBlockedStateAsync(receiver, sessionId, workerId);
                blockCleared = true;
                WriteLine($"[PUMP-{workerId}]   Unblock message completed — session '{sessionId}' UNBLOCKED");
            }

            if (!ok) break; // re-blocked by a new failure; stop draining
        }

        if (!blockCleared && !ct.IsCancellationRequested)
        {
            // The peek showed the match but it wasn't in the received batch (edge race).
            // Cooldown briefly and let the next accept retry.
            BlockedSessionCooldown[sessionId] = DateTime.UtcNow.AddSeconds(1);
        }

        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // ---------------------------------------------------------------
    // SESSION STATE HELPERS
    // ---------------------------------------------------------------

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

    // Marks the session blocked in ASB session state (durable). The scheduled retry
    // is a normal broker message — no registry needed; the hold-back reads this state
    // on every accept and peeks for BlockedMessageId.
    static async Task MarkSessionBlockedAsync(ServiceBusSessionReceiver receiver, string sessionId, string failedMessageId, int workerId)
    {
        var retryAfter = DateTimeOffset.UtcNow + RetryDelay;
        var state = new SessionState
        {
            IsBlocked = true,
            BlockedMessageId = failedMessageId,
            BlockedAt = DateTimeOffset.UtcNow,
            RetryAfter = retryAfter
        };

        await receiver.SetSessionStateAsync(BinaryData.FromBytes(Encoding.UTF8.GetBytes(state.ToJson())));
        WriteLine($"[PUMP-{workerId}] Session state: '{sessionId}' = BLOCKED (msg: {failedMessageId}, retry in {(int)RetryDelay.TotalSeconds}s)");
    }

    // Handle a single message. On success: complete + return true. On failure: schedule
    // a delayed resend (same SessionId + MessageId), complete the original, mark session
    // blocked, return false. On terminal failure (attempts exhausted): DLQ + clear block.
    static async Task<bool> TryHandleAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, ServiceBusReceivedMessage message, string sessionId, int workerId, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetString(message.Body);
        WriteLine($"[PUMP-{workerId}]   Processing '{message.MessageId}' (session '{sessionId}'): {body}");

        var attempts = FailureAttempts.AddOrUpdate(message.MessageId, 1, (_, c) => c + 1);

        try
        {
            if (FailureBudget.TryGetValue(message.MessageId, out var budget) && attempts <= budget)
            {
                throw new InvalidOperationException($"Simulated failure #{attempts} for '{message.MessageId}'");
            }

            await receiver.CompleteMessageAsync(message, ct);
            WriteLine($"[PUMP-{workerId}]   Completed '{message.MessageId}'");
            return true;
        }
        catch (Exception ex)
        {
            WriteLine($"[PUMP-{workerId}]   FAILED '{message.MessageId}': {ex.Message}");

            if (attempts >= MaxAttempts)
            {
                await receiver.DeadLetterMessageAsync(message, deadLetterReason: "MaxRetriesExceeded", deadLetterErrorDescription: ex.Message, cancellationToken: ct);
                await ClearSessionBlockedStateAsync(receiver, sessionId, workerId);
                WriteLine($"[PUMP-{workerId}]   Terminal failure '{message.MessageId}' -> DLQ (attempt {attempts} >= {MaxAttempts}). Backlog may now flow.");
                return false;
            }

            // Schedule a delayed resend. Same SessionId + MessageId so the hold-back
            // finds it via peek-and-search. The scheduled message is normal broker
            // state — survives restart, no registry, no orphan risk.
            var resend = new ServiceBusMessage(message.Body)
            {
                MessageId = message.MessageId,
                SessionId = sessionId,
                ScheduledEnqueueTime = DateTimeOffset.UtcNow + RetryDelay
            };
            resend.ApplicationProperties["RetryCount"] = attempts;

            // Order: mark blocked FIRST (durable hold-back), then schedule, then complete.
            // If we crash after marking blocked, the original re-delivers on lock expiry
            // and the pump re-processes it under the block — correct.
            await MarkSessionBlockedAsync(receiver, sessionId, message.MessageId, workerId);
            await inputQueueSender.SendMessageAsync(resend, ct);
            await receiver.CompleteMessageAsync(message, ct);

            WriteLine($"[PUMP-{workerId}]   Scheduled resend of '{message.MessageId}' (+{(int)RetryDelay.TotalSeconds}s), original completed, session BLOCKED — backlog held.");
            return false;
        }
    }

    static async Task ClearSessionBlockedStateAsync(ServiceBusSessionReceiver receiver, string sessionId, int workerId)
    {
        await receiver.SetSessionStateAsync(null as BinaryData);
        BlockedSessionCooldown.TryRemove(sessionId, out _);
        WriteLine($"[PUMP-{workerId}] Session state: '{sessionId}' = UNBLOCKED");
    }

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

    static bool IsSessionInCooldown(string sessionId)
    {
        if (BlockedSessionCooldown.TryGetValue(sessionId, out var cooldownUntil))
        {
            if (DateTime.UtcNow < cooldownUntil)
                return true;
            BlockedSessionCooldown.TryRemove(sessionId, out _);
        }
        return false;
    }

    // ---------------------------------------------------------------
    // DLQ RETRY PROCESSOR
    // ---------------------------------------------------------------

    // Reads from the dead-letter queue and re-sends messages back to the input queue.
    // Simulates what ServiceControl would do. Re-sent messages land as normal messages
    // behind the backlog; the peek-and-search hold-back handles them identically to
    // scheduled resends (same SessionId + MessageId identity matching).
    static async Task RunDlqRetryProcessorAsync(ServiceBusClient client, CancellationToken ct)
    {
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

            var retryMessage = new ServiceBusMessage(message);
            retryMessage.SessionId = message.SessionId;

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

            await sender.SendMessageAsync(retryMessage, ct);
            WriteLine($"[DLQ] Re-sent '{message.MessageId}' to input queue (session '{message.SessionId}')");

            await args.CompleteMessageAsync(message, ct);
            WriteLine($"[DLQ] Completed DLQ message '{message.MessageId}'");

            if (!string.IsNullOrEmpty(message.SessionId))
            {
                BlockedSessionCooldown.TryRemove(message.SessionId, out _);
                WriteLine($"[DLQ] Cleared cooldown for session '{message.SessionId}'");
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

    // ---------------------------------------------------------------
    // TEST MESSAGES
    // ---------------------------------------------------------------

    static async Task PublishTestMessagesAsync(ServiceBusClient client)
    {
        await using var salesSender = client.CreateSender(Prepare.SalesTopicName);
        await using var inventorySender = client.CreateSender(Prepare.InventoryTopicName);

        WriteLine();
        WriteLine("=== Publishing sales messages ===");

        var m1 = new ServiceBusMessage("Order received for Customer-123")
        { MessageId = "cust123-msg1", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m1);
        WriteLine($"  Published '{m1.MessageId}' (session: Customer-123)");

        var m2 = new ServiceBusMessage("Payment processing for Customer-123")
        { MessageId = "cust123-msg2", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m2);
        WriteLine($"  Published '{m2.MessageId}' (session: Customer-123) [WILL FAIL]");

        var m3 = new ServiceBusMessage("Shipping for Customer-123")
        { MessageId = "cust123-msg3", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m3);
        WriteLine($"  Published '{m3.MessageId}' (session: Customer-123)");

        var m4 = new ServiceBusMessage("Order received for Customer-456")
        { MessageId = "cust456-msg1", SessionId = "Customer-456" };
        await salesSender.SendMessageAsync(m4);
        WriteLine($"  Published '{m4.MessageId}' (session: Customer-456)");

        var m5 = new ServiceBusMessage("Payment processed for Customer-456")
        { MessageId = "cust456-msg2", SessionId = "Customer-456" };
        await salesSender.SendMessageAsync(m5);
        WriteLine($"  Published '{m5.MessageId}' (session: Customer-456)");

        var m6 = new ServiceBusMessage("Order for Customer-789")
        { MessageId = "cust789-msg1", SessionId = "Customer-789" };
        await salesSender.SendMessageAsync(m6);
        WriteLine($"  Published '{m6.MessageId}' (session: Customer-789) [WILL FAIL]");

        WriteLine();
        WriteLine("=== Publishing inventory messages ===");

        var i1 = new ServiceBusMessage("Stock level check for SKU-001")
        { MessageId = "stock001-msg1", SessionId = "Stock-001" };
        await inventorySender.SendMessageAsync(i1);
        WriteLine($"  Published '{i1.MessageId}' (session: Stock-001)");

        var i2 = new ServiceBusMessage("Stock reservation for SKU-001")
        { MessageId = "stock001-msg2", SessionId = "Stock-001" };
        await inventorySender.SendMessageAsync(i2);
        WriteLine($"  Published '{i2.MessageId}' (session: Stock-001)");

        var i3 = new ServiceBusMessage("Stock level check for SKU-002")
        { MessageId = "stock002-msg1", SessionId = "Stock-002" };
        await inventorySender.SendMessageAsync(i3);
        WriteLine($"  Published '{i3.MessageId}' (session: Stock-002)");

        WriteLine("=== Publishing complete ===");
        WriteLine();
    }

    // Concurrent publisher: emits extra messages into Customer-123 AFTER msg2 has
    // (hopefully) already failed and been scheduled for resend. These land behind
    // msg3 in the session. The hold-back must prevent ALL of them from completing
    // before msg2's scheduled retry succeeds.
    static async Task RunConcurrentPublisherAsync(ServiceBusClient client, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }

        await using var sender = client.CreateSender(Prepare.SalesTopicName);
        for (int i = 5; i <= 7; i++)
        {
            if (ct.IsCancellationRequested) return;
            var m = new ServiceBusMessage($"Concurrent update #{i - 4} for Customer-123")
            { MessageId = $"cust123-msg{i}", SessionId = "Customer-123" };
            await sender.SendMessageAsync(m, ct);
            WriteLine($"  [CONCURRENT] Published '{m.MessageId}' (session: Customer-123)");
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
        }
    }
}

// ---------------------------------------------------------------
// SESSION STATE (versioned JSON envelope)
// ---------------------------------------------------------------

public record SessionState
{
    public bool IsBlocked { get; init; }
    public string? BlockedMessageId { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }

    public static SessionState Default => new() { IsBlocked = false };

    public string ToJson()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 3,
            transport = new
            {
                blocked = IsBlocked,
                blockedMessageId = BlockedMessageId,
                blockedAt = BlockedAt?.ToString("O"),
                retryAfter = RetryAfter?.ToString("O")
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
                    : null,
                RetryAfter = t.TryGetProperty("retryAfter", out var ra) && ra.ValueKind == System.Text.Json.JsonValueKind.String
                    ? DateTimeOffset.Parse(ra.GetString()!)
                    : null
            };
        }
        catch
        {
            return Default;
        }
    }
}
