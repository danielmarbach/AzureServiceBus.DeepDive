#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using static System.Console;

namespace SubscriptionBridge;

// Spike for the session-enabled endpoint topology. This is a spike, not production
// code — it exists to falsify the design rather than to be shipped. The shape we're
// checking against a live namespace:
//
//   publisher
//     -> topic (orders)
//       -> session-enabled subscription (sales-sub, no ForwardTo)
//         -> transport-owned subscription bridge (session processor)
//           -> session-enabled endpoint input queue (sales-input)
//             -> input queue session pump (manual AcceptNextSessionAsync)
//               -> simulated handler
//
// Recoverability uses scheduled resend plus a session-state hold-back. Each retry
// keeps its logical identity but gets a deterministic physical MessageId.
//
// On failure the pump schedules the retry, persists the expected physical identity,
// completes the original, and releases the session. The hold-back processes only
// that expected retry before allowing the unsettled backlog to re-flow.
internal class Program
{
    static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString")!;

    // Keeps blocked sessions out of the accept loop until their retry is due. We
    // derive this from RetryAfter rather than a flat duration, so we neither spin on
    // blocked sessions nor wake up too early.
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
    const string LogicalMessageIdProperty = "Spike.LogicalMessageId";
    const string RetryCountProperty = "Spike.RetryCount";
    const string ManualRetryCountProperty = "Spike.ManualRetryCount";

    static readonly Dictionary<string, int> FailureBudget = new()
    {
        ["cust123-msg2"] = 1,   // fails attempt 1, succeeds on the scheduled retry
        ["cust789-msg1"] = 2,   // fails attempts 1 & 2, succeeds on the second scheduled retry
    };

    // "restart" runs the restart-durability scenario; anything else is the normal
    // 60s run. The restart scenario publishes, waits for msg2 to fail and schedule
    // its retry, tears down the pump and client (a stand-in for a process stop),
    // sits with no pump running past the retry delay, then brings up a fresh pump on
    // a new client. The claim we want to falsify: the scheduled message (broker
    // state) and the blocked marker (session state) both survive, and the new pump's
    // hold-back reconstructs order from durable state with nothing orphaned. It's a
    // faithful stand-in for a restart because the only in-memory state we lose is the
    // cooldown map, which is derived from RetryAfter in session state.
    static async Task Main(string[] args)
    {
        var restartMode = args.Length > 0 && string.Equals(args[0], "restart", StringComparison.OrdinalIgnoreCase);
        var stretchMode = args.Length > 0 && string.Equals(args[0], "stretch", StringComparison.OrdinalIgnoreCase);
        await using var cleanup = await Prepare.Stage(ConnectionString);

        if (restartMode)
        {
            await RunRestartScenarioAsync();
        }
        else
        {
            await RunNormalScenarioAsync(stretchMode);
        }

        WriteLine();
        WriteLine("=== Spike complete ===");
    }

    static async Task RunNormalScenarioAsync(bool stretchBacklog = false)
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

        await PublishTestMessagesAsync(client, stretchBacklog);

        // Concurrent producer: keeps feeding Customer-123 while msg2's retry is in
        // flight. This is the falsifiable part — the hold-back has to stop every one
        // of these (and msg3) from completing before msg2's scheduled retry succeeds.
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

    // Restart-durability scenario. Phase 1 gets a blocked session with a scheduled
    // retry in flight, then everything stops. Phase 2 starts a brand-new pump on a
    // brand-new client after the retry delay has elapsed, and has to recover.
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

        // Wait long enough that cust123-msg1 completes, cust123-msg2 fails, the session
        // is marked blocked, and the +6s scheduled retry is in flight. At 4s after
        // publish that's the state: msg2 failed, block set, retry scheduled, session
        // released. cust789 ends up in the same place. We don't start the concurrent
        // publisher here — we want a fixed backlog so the recovery assertion is
        // deterministic.
        WriteLine("[RESTART] Letting failures establish + scheduling retries...");
        await Task.Delay(TimeSpan.FromSeconds(4));

        WriteLine("[RESTART] === STOP: tearing down pump + client (simulating process stop) ===");
        pumpCts1.Cancel();
        bridgeCts.Cancel();
        try { await pumpTask1; } catch (OperationCanceledException) { }
        try { await salesBridgeTask1; } catch (OperationCanceledException) { }
        try { await inventoryBridgeTask1; } catch (OperationCanceledException) { }
        await client1.DisposeAsync();

        // The in-memory cooldown is gone now. The broker still holds the scheduled
        // retries (normal broker state), the blocked-session markers (ASB session
        // state), and the backlogs (msg3 and friends). Sit here with no pump past the
        // retry delay.
        WriteLine("[RESTART] === DOWN: no pump running, waiting out retry delay ===");
        await Task.Delay(TimeSpan.FromSeconds(10));

        // ---- Phase 2: fresh client, fresh pump, empty cooldown. Must recover. ----
        WriteLine("[RESTART] === START: fresh pump on new client — must recover from durable state ===");
        await using var client2 = NewClient();
        var pumpCts2 = new CancellationTokenSource();
        var pumpTask2 = RunInputQueuePumpAsync(client2, pumpCts2.Token);

        // Give recovery room to run: pull the scheduled retries, clear blocks, drain
        // the backlog.
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

        // One sender for scheduled resends, shared across all workers.
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

                // Cooldown check: skip blocked sessions whose retry isn't due yet, so we
                // don't hot-spin on them during the retry delay.
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

    // Processes a single session, two paths:
    //
    // BLOCKED: the session is pinned to ExpectedRetryMessageId. The pump processes
    // only that physical retry before allowing the unsettled backlog to re-flow.
    //
    // CLEAR: ordinary FIFO receive and process.
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
            if (!ok) break; // a failure blocked the session; stop draining this batch
        }

        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // The persisted peek frontier skips backlog already ruled out. Physical retry
    // identity remains unambiguous even when logical identities repeat.
    static async Task ProcessBlockedSessionAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, string sessionId, SessionState sessionState, int workerId, CancellationToken ct)
    {
        WriteLine($"[PUMP-{workerId}] Session '{sessionId}' BLOCKED on logical '{sessionState.LogicalMessageId}', waiting for '{sessionState.ExpectedRetryMessageId}'.");

        // The retry cannot be visible before RetryAfter, so scanning earlier only spins.
        if (sessionState.RetryAfter is DateTimeOffset retryAfter && retryAfter > DateTimeOffset.UtcNow)
        {
            BlockedSessionCooldown[sessionId] = retryAfter.UtcDateTime;
            var wait = retryAfter - DateTimeOffset.UtcNow;
            WriteLine($"[PUMP-{workerId}]   Retry not due until +{(int)Math.Ceiling(wait.TotalSeconds)}s — cooldown. Releasing.");
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        // A scheduled message receives its final sequence number when it is enqueued.
        // A frontier persisted before that point is therefore safe to resume after.
        long? matchSeq = null;
        long? lastPeeked = null;
        {
            long? fromSeq = sessionState.LastPeekedSequenceNumber is long frontier
                ? frontier + 1
                : null;
            const int peekBatch = 32;
            while (!ct.IsCancellationRequested)
            {
                var peeked = await receiver.PeekMessagesAsync(peekBatch, fromSeq, ct);
                if (peeked.Count == 0)
                    break; // session exhausted; retry not visible yet

                lastPeeked = peeked.Max(m => m.SequenceNumber);

                var match = peeked.FirstOrDefault(m =>
                    string.Equals(m.MessageId, sessionState.ExpectedRetryMessageId, StringComparison.Ordinal));
                if (match != null)
                {
                    matchSeq = match.SequenceNumber;
                    WriteLine($"[PUMP-{workerId}]   Found retry at seq {matchSeq} (scan from seq {fromSeq ?? 0}).");
                    break;
                }

                // Partial page => we've reached the end of the session; retry not visible.
                if (peeked.Count < peekBatch)
                    break;

                // Advance the frontier past what we just ruled out.
                fromSeq = lastPeeked + 1;
            }
        }

        if (matchSeq == null)
        {
            // Persist the exhausted scan so the next accept resumes after this frontier.
            if (lastPeeked is long lp && lp > (sessionState.LastPeekedSequenceNumber ?? 0))
            {
                var updated = sessionState with { LastPeekedSequenceNumber = lp };
                await receiver.SetSessionStateAsync(BinaryData.FromBytes(Encoding.UTF8.GetBytes(updated.ToJson())));
                WriteLine($"[PUMP-{workerId}]   Persisted peek frontier at seq {lp}.");
            }
            BlockedSessionCooldown[sessionId] = DateTime.UtcNow.AddSeconds(1);
            WriteLine($"[PUMP-{workerId}]   Retry due but not visible — cooldown 1s. Releasing.");
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        WriteLine($"[PUMP-{workerId}]   Found blocked message (seq {matchSeq}) — draining to it...");

        // Receive forward until the retry turns up, leaving the backlog ahead of it
        // LOCKED but unsettled. We deliberately do NOT abandon the backlog: per ASB
        // session semantics, closing a session with unsettled messages re-flows them
        // without incrementing DeliveryCount, so a retry that fails again re-runs this
        // hold-back without burning a delivery on every backlog message. Abandoning
        // would re-serve the same prefix to the head on every pass and inflate
        // DeliveryCount until the backlog dead-letters without ever being processed.
        // Receiving forward (instead of one 32-message batch) is also what lets the
        // hold-back reach a retry buried deeper than one batch — the abandoned-prefix
        // approach could never get past the re-flowed head.
        ServiceBusReceivedMessage? retryMessage = null;
        while (!ct.IsCancellationRequested)
        {
            var batch = await receiver.ReceiveMessagesAsync(
                maxMessages: 32,
                maxWaitTime: TimeSpan.FromSeconds(2),
                ct);

            if (batch.Count == 0)
                break; // session exhausted; the retry we peeked is gone — edge race

            retryMessage = batch.FirstOrDefault(m =>
                string.Equals(m.MessageId, sessionState.ExpectedRetryMessageId, StringComparison.Ordinal));
            if (retryMessage != null)
                break;
        }

        if (retryMessage == null)
        {
            // We peeked the match but it didn't turn up in the received batches — an
            // edge race. Cooldown briefly and let the next accept try again. The locked
            // backlog re-flows on close, so nothing is lost.
            BlockedSessionCooldown[sessionId] = DateTime.UtcNow.AddSeconds(1);
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        // Process ONLY the retry. Everything else in the session — the locked backlog
        // ahead of it and any messages we received past it — re-flows on close and is
        // processed in original order on the next accept. Processing past the retry in
        // this accept would let post-retry messages jump ahead of the held-back backlog.
        var ok = await TryHandleAsync(receiver, inputQueueSender, retryMessage, sessionId, workerId, ct);

        if (ok)
        {
            await ClearSessionBlockedStateAsync(receiver, sessionId, workerId);
            WriteLine($"[PUMP-{workerId}]   Unblock message completed — session '{sessionId}' UNBLOCKED");
        }

        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // ---------------------------------------------------------------
    // SESSION STATE HELPERS
    // ---------------------------------------------------------------

    static async Task<SessionState> ReadSessionStateAsync(ServiceBusSessionReceiver receiver, CancellationToken ct)
    {
        var binaryState = await receiver.GetSessionStateAsync(ct);
        return binaryState == null
            ? SessionState.Default
            : SessionState.FromJson(Encoding.UTF8.GetString(binaryState));
    }

    static async Task MarkSessionBlockedAsync(ServiceBusSessionReceiver receiver, string sessionId, string logicalMessageId, string expectedRetryMessageId, DateTimeOffset retryAfter, int workerId)
    {
        var state = new SessionState
        {
            IsBlocked = true,
            LogicalMessageId = logicalMessageId,
            ExpectedRetryMessageId = expectedRetryMessageId,
            BlockedAt = DateTimeOffset.UtcNow,
            RetryAfter = retryAfter
        };

        await receiver.SetSessionStateAsync(BinaryData.FromBytes(Encoding.UTF8.GetBytes(state.ToJson())));
        WriteLine($"[PUMP-{workerId}] Session state: '{sessionId}' = BLOCKED (logical: {logicalMessageId}, expected: {expectedRetryMessageId})");
    }

    static async Task<bool> TryHandleAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, ServiceBusReceivedMessage message, string sessionId, int workerId, CancellationToken ct)
    {
        var logicalMessageId = GetLogicalMessageId(message);
        var attempt = GetCount(message, RetryCountProperty) + 1;
        var body = Encoding.UTF8.GetString(message.Body);
        WriteLine($"[PUMP-{workerId}]   Processing logical '{logicalMessageId}' via '{message.MessageId}' (attempt {attempt}, session '{sessionId}'): {body}");

        try
        {
            if (FailureBudget.TryGetValue(logicalMessageId, out var budget) && attempt <= budget)
                throw new InvalidOperationException($"Simulated failure #{attempt} for '{logicalMessageId}'");

            await receiver.CompleteMessageAsync(message, ct);
            WriteLine($"[PUMP-{workerId}]   Completed logical '{logicalMessageId}' via '{message.MessageId}'");
            return true;
        }
        catch (Exception ex)
        {
            WriteLine($"[PUMP-{workerId}]   FAILED logical '{logicalMessageId}' via '{message.MessageId}': {ex.Message}");

            if (attempt >= MaxAttempts)
            {
                await receiver.DeadLetterMessageAsync(message, deadLetterReason: "MaxRetriesExceeded", deadLetterErrorDescription: ex.Message, cancellationToken: ct);
                await ClearSessionBlockedStateAsync(receiver, sessionId, workerId);
                WriteLine($"[PUMP-{workerId}]   Terminal failure '{logicalMessageId}' -> DLQ (attempt {attempt} >= {MaxAttempts}). Backlog may now flow.");
                return false;
            }

            var retryMessageId = CreatePhysicalMessageId("delayed", sessionId, logicalMessageId, attempt);
            var resend = new ServiceBusMessage(message.Body)
            {
                MessageId = retryMessageId,
                SessionId = sessionId
            };
            resend.ApplicationProperties[LogicalMessageIdProperty] = logicalMessageId;
            resend.ApplicationProperties[RetryCountProperty] = attempt;

            var scheduledEnqueueTime = DateTimeOffset.UtcNow + RetryDelay;
            await inputQueueSender.ScheduleMessageAsync(resend, scheduledEnqueueTime, ct);
            await MarkSessionBlockedAsync(receiver, sessionId, logicalMessageId, retryMessageId, scheduledEnqueueTime, workerId);
            await receiver.CompleteMessageAsync(message, ct);

            WriteLine($"[PUMP-{workerId}]   Scheduled '{retryMessageId}' for logical '{logicalMessageId}' (+{(int)RetryDelay.TotalSeconds}s), original completed, session BLOCKED.");
            return false;
        }
    }

    static string GetLogicalMessageId(ServiceBusReceivedMessage message)
    {
        if (!message.ApplicationProperties.TryGetValue(LogicalMessageIdProperty, out var value))
            return message.MessageId;

        return value is string logicalMessageId && !string.IsNullOrWhiteSpace(logicalMessageId)
            ? logicalMessageId
            : throw new InvalidOperationException($"'{LogicalMessageIdProperty}' must be a non-empty string.");
    }

    static int GetCount(ServiceBusReceivedMessage message, string propertyName)
    {
        if (!message.ApplicationProperties.TryGetValue(propertyName, out var value))
            return 0;

        return value switch
        {
            int count when count >= 0 => count,
            long count when count is >= 0 and <= int.MaxValue => (int)count,
            _ => throw new InvalidOperationException($"'{propertyName}' must be a non-negative integer.")
        };
    }

    static string CreatePhysicalMessageId(string purpose, string sessionId, string logicalMessageId, int attempt)
    {
        var identity = Encoding.UTF8.GetBytes($"{purpose}\n{sessionId}\n{logicalMessageId}\n{attempt}");
        return $"{purpose}-{Convert.ToHexString(SHA256.HashData(identity))}";
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

    // This entity-DLQ loop stands in for a ServiceControl retry. The production
    // shape has a separate error queue and operational retry policy.
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

            var logicalMessageId = GetLogicalMessageId(message);
            var manualRetryCount = GetCount(message, ManualRetryCountProperty) + 1;
            if (manualRetryCount > 3)
            {
                WriteLine($"[DLQ] Logical message '{logicalMessageId}' exceeded max manual retries ({manualRetryCount}).");
                await args.DeadLetterMessageAsync(message, "MaxRetriesExceeded", "Exceeded maximum manual retry count", ct);
                return;
            }

            var retryMessage = new ServiceBusMessage(message)
            {
                MessageId = CreatePhysicalMessageId("manual", message.SessionId!, logicalMessageId, manualRetryCount),
                SessionId = message.SessionId
            };
            retryMessage.ApplicationProperties[LogicalMessageIdProperty] = logicalMessageId;
            retryMessage.ApplicationProperties[RetryCountProperty] = 0;
            retryMessage.ApplicationProperties[ManualRetryCountProperty] = manualRetryCount;

            WriteLine($"[DLQ] Manual retry #{manualRetryCount} for logical '{logicalMessageId}' as '{retryMessage.MessageId}'");

            await sender.SendMessageAsync(retryMessage, ct);
            WriteLine($"[DLQ] Re-sent logical '{logicalMessageId}' to input queue (session '{message.SessionId}')");

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

    static async Task PublishTestMessagesAsync(ServiceBusClient client, bool stretchBacklog = false)
    {
        await using var salesSender = client.CreateSender(Prepare.SalesTopicName);
        await using var inventorySender = client.CreateSender(Prepare.InventoryTopicName);

        WriteLine();
        WriteLine("=== Publishing sales messages ===");

        var m1 = new ServiceBusMessage("Order received for Customer-123")
        { MessageId = "cust123-msg1", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m1);
        WriteLine($"  Published '{m1.MessageId}' (session: Customer-123)");

        // Stretch mode: publish a large backlog into Customer-123 BEFORE msg2 runs, so
        // msg2's scheduled retry lands ~40 messages deep — past one peek page (32). This
        // exercises the frontier-paging peek; the old head-only peek would never find it.
        if (stretchBacklog)
        {
            WriteLine("  [STRETCH] Publishing 40 backlog messages into Customer-123 BEFORE msg2...");
            for (int i = 100; i < 140; i++)
            {
                var bm = new ServiceBusMessage($"Backlog filler #{i - 99} for Customer-123")
                { MessageId = $"cust123-backlog-{i}", SessionId = "Customer-123" };
                await salesSender.SendMessageAsync(bm);
            }
            WriteLine("  [STRETCH] 40 backlog messages published.");
        }

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

    // Concurrent publisher: pushes extra messages into Customer-123 after msg2 has —
    // hopefully — already failed and been scheduled for resend. They land behind msg3
    // in the session. The hold-back has to stop every one of them completing before
    // msg2's scheduled retry succeeds.
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
//
// For the spike this only models the transport side of the envelope. The real
// transport would carry a separate "user" section that handler code owns and that
// recoverability leaves untouched — see the topology doc for the full shape.

public record SessionState
{
    public bool IsBlocked { get; init; }
    public string? LogicalMessageId { get; init; }
    public string? ExpectedRetryMessageId { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }
    public long? LastPeekedSequenceNumber { get; init; }

    public static SessionState Default => new() { IsBlocked = false };

    public string ToJson()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 6,
            transport = new
            {
                blocked = IsBlocked,
                logicalMessageId = LogicalMessageId,
                expectedRetryMessageId = ExpectedRetryMessageId,
                blockedAt = BlockedAt?.ToString("O"),
                retryAfter = RetryAfter?.ToString("O"),
                lastPeekedSequenceNumber = LastPeekedSequenceNumber
            }
        });
    }

    public static SessionState FromJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("version").GetInt32();
        if (version != 6)
            throw new InvalidOperationException($"Unsupported session-state version '{version}'.");

        var transport = root.GetProperty("transport");
        if (!transport.GetProperty("blocked").GetBoolean())
            return Default;

        var logicalMessageId = transport.GetProperty("logicalMessageId").GetString();
        var expectedRetryMessageId = transport.GetProperty("expectedRetryMessageId").GetString();
        if (string.IsNullOrWhiteSpace(logicalMessageId) || string.IsNullOrWhiteSpace(expectedRetryMessageId))
            throw new InvalidOperationException("Blocked session state requires logical and physical retry identities.");

        var blockedAt = DateTimeOffset.Parse(transport.GetProperty("blockedAt").GetString()!);
        var retryAfter = DateTimeOffset.Parse(transport.GetProperty("retryAfter").GetString()!);

        return new SessionState
        {
            IsBlocked = true,
            LogicalMessageId = logicalMessageId,
            ExpectedRetryMessageId = expectedRetryMessageId,
            BlockedAt = blockedAt,
            RetryAfter = retryAfter,
            LastPeekedSequenceNumber = transport.TryGetProperty("lastPeekedSequenceNumber", out var lastPeeked) && lastPeeked.ValueKind == System.Text.Json.JsonValueKind.Number
                ? lastPeeked.GetInt64()
                : null
        };
    }
}
