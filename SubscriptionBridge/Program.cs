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
// keeps its logical identity but gets a distinct physical MessageId, deterministic per attempt.
//
// On failure the pump atomically schedules the retry, persists the expected
// physical identity, and completes the original before releasing the session. A
// terminal failure instead dead-letters the current message and persists the
// deterministic manual identity while keeping the session blocked. The hold-back
// processes only that expected retry before allowing the unsettled backlog to re-flow.
internal class Program
{
    static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString")!;

    // The queue uses Service Bus' default one-minute lock duration. Keep each broker
    // operation bounded so a three-operation transaction has room to finish before
    // the session/message lock expires.
    const int ServiceBusTryTimeoutSeconds = 10;
    static readonly TimeSpan ServiceBusTransactionTimeout = TimeSpan.FromSeconds(45);
    static readonly TimeSpan SessionLockRenewalThreshold = TimeSpan.FromSeconds(20);
    static readonly TimeSpan OrdinaryCompletionLockBudget =
        TimeSpan.FromSeconds(ServiceBusTryTimeoutSeconds + 3);
    static readonly TimeSpan TransactionLockBudget =
        ServiceBusTransactionTimeout + TimeSpan.FromSeconds(ServiceBusTryTimeoutSeconds);

    // This observer belongs to the executable scenarios only. It records a
    // completion after broker settlement; it is not a transport correctness mechanism.
    static readonly ScenarioCompletionObserver ScenarioObserver = new();

    // --- Delayed-retry configuration (scheduled resend) ---
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(6);
    const int MaxAttempts = 5;
    internal const string LogicalMessageIdProperty = "Spike.LogicalMessageId";
    internal const string RetryCountProperty = "Spike.RetryCount";
    internal const string ManualRetryCountProperty = "Spike.ManualRetryCount";
    internal const string TerminalScenarioProperty = "Spike.TerminalScenario";
    const string TerminalScenarioLogicalMessageId = "terminal-msg1";
    const int MaxManualRetries = 3;

    static readonly Dictionary<string, int> FailureBudget = new()
    {
        ["cust123-msg2"] = 1,   // fails attempt 1, succeeds on the scheduled retry
        ["cust789-msg1"] = 2,   // fails attempts 1 & 2, succeeds on the second scheduled retry
    };

    // "restart" runs the restart-durability scenario; anything else runs the normal
    // scenario. The restart scenario publishes, waits for msg2 to fail and schedule
    // its retry, tears down the pump and client, waits until the scheduled retry is due,
    // then brings up a fresh pump on a new client. The scenario observer checks the
    // durable recovery result; it is not a transport correctness mechanism.
    static async Task Main(string[] args)
    {
        var restartMode = args.Length > 0 && string.Equals(args[0], "restart", StringComparison.OrdinalIgnoreCase);
        var stretchMode = args.Length > 0 && string.Equals(args[0], "stretch", StringComparison.OrdinalIgnoreCase);
        var competingMode = args.Length > 0 && string.Equals(args[0], "competing", StringComparison.OrdinalIgnoreCase);
        var terminalMode = args.Length > 0 && string.Equals(args[0], "terminal", StringComparison.OrdinalIgnoreCase);
        await using var cleanup = await Prepare.Stage(ConnectionString);
        ScenarioObserver.Clear();

        if (terminalMode)
        {
            await RunTerminalBringBackScenarioAsync();
        }
        else if (restartMode)
        {
            await RunRestartScenarioAsync();
        }
        else
        {
            await RunNormalScenarioAsync(stretchMode, competingMode);
        }

        WriteLine();
        WriteLine("=== Spike complete ===");
    }

    static async Task RunNormalScenarioAsync(bool stretchBacklog = false, bool competingConsumers = false)
    {
        await using var client = NewClient();
        await using var competingClient = competingConsumers ? NewClient() : null;

        var bridgeCts = new CancellationTokenSource();
        await using var stretchPumpClient = stretchBacklog ? NewPumpClient() : null;
        var salesBridgeTask = RunSubscriptionBridgeAsync(Prepare.SalesTopicName, Prepare.SalesSub, "Sales", bridgeCts.Token);
        var inventoryBridgeTask = RunSubscriptionBridgeAsync(Prepare.InventoryTopicName, Prepare.InventorySub, "Inventory", bridgeCts.Token);

        var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.CompletedTask;
        var competingPumpTask = Task.CompletedTask;
        if (!stretchBacklog)
        {
            pumpTask = RunInputQueuePumpAsync(client, pumpCts.Token);
            competingPumpTask = competingClient is null
                ? Task.CompletedTask
                : RunInputQueuePumpAsync(competingClient, pumpCts.Token);
        }

        var dlqCts = new CancellationTokenSource();
        // Stretch isolates bridge fill and hold-back behavior. It has no terminal
        // failure, so starting the DLQ processor would add an unrelated receiver
        // while the bridge is still filling the 40-message backlog.
        var dlqTask = stretchBacklog
            ? Task.CompletedTask
            : RunDlqRetryProcessorAsync(client, dlqCts.Token);

        await Task.Delay(2000);

        await PublishTestMessagesAsync(client, stretchBacklog);

        if (stretchBacklog)
        {
            WriteLine("[STRETCH] Waiting for the bridges to fill the input queue before starting the pump...");
            await using var probeClient = NewProbeClient();
            await WaitForSessionMessagesAsync(probeClient, "Customer-123", expectedCount: 43, TimeSpan.FromSeconds(90));
            WriteLine("[STRETCH] Input queue backlog is durably established.");
            pumpTask = RunInputQueuePumpAsync(stretchPumpClient ?? client, pumpCts.Token);
            await WaitForBlockedSessionAsync(probeClient, "Customer-123", TimeSpan.FromSeconds(60));
            WriteLine("[STRETCH] Transactional block is durably established.");
        }

        // Concurrent producer: keeps feeding Customer-123 while msg2's retry is in
        // flight. The scenario assertion requires the retry to complete first.
        var concurrentPubTask = RunConcurrentPublisherAsync(client, bridgeCts.Token);
        var expected = CreateScenarioPlan(stretchBacklog);

        WriteLine("[MAIN] Waiting for the scenario assertions...");
        await WaitForScenarioAsync(expected, TimeSpan.FromSeconds(90));

        pumpCts.Cancel();
        dlqCts.Cancel();
        bridgeCts.Cancel();

        await ObserveCancellationAsync(pumpTask, "input pump");
        await ObserveCancellationAsync(competingPumpTask, "competing input pump");
        await ObserveCancellationAsync(dlqTask, "DLQ processor");
        await ObserveCancellationAsync(salesBridgeTask, "sales bridge");
        await ObserveCancellationAsync(inventoryBridgeTask, "inventory bridge");
        await ObserveCancellationAsync(concurrentPubTask, "concurrent publisher");
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

        // The blocked state is the signal that the scheduled retry, state write, and
        // original completion became visible together. We do not start the concurrent
        // publisher here, so the recovery assertion has a fixed backlog.
        WriteLine("[RESTART] Waiting for the transactional block to be established...");
        var blockedState = await WaitForBlockedSessionAsync(client1, "Customer-123", TimeSpan.FromSeconds(30));
        WriteLine("[RESTART] Transactional block is durably established.");

        WriteLine("[RESTART] === STOP: tearing down pump + client (simulating process stop) ===");
        pumpCts1.Cancel();
        bridgeCts.Cancel();
        await ObserveCancellationAsync(pumpTask1, "restart phase-one input pump");
        await ObserveCancellationAsync(salesBridgeTask1, "restart phase-one sales bridge");
        await ObserveCancellationAsync(inventoryBridgeTask1, "restart phase-one inventory bridge");
        await client1.DisposeAsync();

        // The in-memory cooldown is gone now. The broker still holds the scheduled
        // retry, blocked-session marker, and backlog. Wait for the broker schedule to
        // become receivable rather than assuming a fixed processing duration.
        WriteLine("[RESTART] === DOWN: no pump running, waiting until retry is due ===");
        await WaitUntilAsync(blockedState.RetryAfter!.Value, TimeSpan.FromSeconds(30));

        // ---- Phase 2: fresh client, fresh pump, empty cooldown. Must recover. ----
        WriteLine("[RESTART] === START: fresh pump on new client — must recover from durable state ===");
        await using var client2 = NewClient();
        var pumpCts2 = new CancellationTokenSource();
        var pumpTask2 = RunInputQueuePumpAsync(client2, pumpCts2.Token);

        // Pull the scheduled retry, clear the block, and drain the backlog. The
        // observer checks this without relying on a fixed sleep.
        WriteLine("[RESTART] Waiting for the restart assertions...");
        await WaitForScenarioAsync(CreateRestartScenarioPlan(), TimeSpan.FromSeconds(90));

        pumpCts2.Cancel();
        await ObserveCancellationAsync(pumpTask2, "restart phase-two input pump");
    }

    // Terminal bring-back scenario: the first message fails every automated attempt,
    // remains the durable session hold-back while it is in the DLQ, and succeeds only
    // after the DLQ processor creates the expected manual physical identity. The
    // simulator uses durable message metadata (not process memory) to distinguish that
    // first manual recovery cycle.
    static async Task RunTerminalBringBackScenarioAsync()
    {
        await using var client = NewClient();
        var pumpCts = new CancellationTokenSource();
        var dlqCts = new CancellationTokenSource();
        var pumpTask = RunInputQueuePumpAsync(client, pumpCts.Token);
        var dlqTask = RunDlqRetryProcessorAsync(client, dlqCts.Token);

        await using var sender = client.CreateSender(Prepare.InputQueueName);
        await sender.SendMessagesAsync(
        [
            new ServiceBusMessage("Terminal failure")
            {
                MessageId = TerminalScenarioLogicalMessageId,
                SessionId = "Terminal-001",
                ApplicationProperties = { [TerminalScenarioProperty] = true }
            },
            new ServiceBusMessage("Later message 2") { MessageId = "terminal-msg2", SessionId = "Terminal-001" },
            new ServiceBusMessage("Later message 3") { MessageId = "terminal-msg3", SessionId = "Terminal-001" }
        ]);

        WriteLine("[TERMINAL] Waiting for terminal DLQ bring-back assertions...");
        await WaitForScenarioAsync(CreateTerminalScenarioPlan(), TimeSpan.FromSeconds(120));

        pumpCts.Cancel();
        dlqCts.Cancel();
        await ObserveCancellationAsync(pumpTask, "terminal input pump");
        await ObserveCancellationAsync(dlqTask, "terminal DLQ processor");
    }

    static ServiceBusClient NewClient() => new(ConnectionString, new ServiceBusClientOptions
    {
        TransportType = ServiceBusTransportType.AmqpWebSockets,
        RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(ServiceBusTryTimeoutSeconds) },
        EnableCrossEntityTransactions = true
    });

    static ServiceBusClient NewProbeClient() => new(ConnectionString, new ServiceBusClientOptions
    {
        TransportType = ServiceBusTransportType.AmqpWebSockets,
        RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(ServiceBusTryTimeoutSeconds) }
    });

    static ServiceBusClient NewPumpClient() => new(ConnectionString, new ServiceBusClientOptions
    {
        TransportType = ServiceBusTransportType.AmqpWebSockets,
        RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(ServiceBusTryTimeoutSeconds) }
    });

    static async Task ObserveCancellationAsync(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            WriteLine($"[MAIN] {operation} stopped after cancellation.");
        }
    }

    // ---------------------------------------------------------------
    // SUBSCRIPTION BRIDGE
    // ---------------------------------------------------------------

    static async Task RunSubscriptionBridgeAsync(string topicName, string subscriptionName, string label, CancellationToken ct)
    {
        var bridgeClient = new ServiceBusClient(ConnectionString, new ServiceBusClientOptions
        {
            TransportType = ServiceBusTransportType.AmqpWebSockets,
            RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(ServiceBusTryTimeoutSeconds) },
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
                PrefetchCount = 0,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        bridgeProcessor.ProcessMessageAsync += async args =>
        {
            var message = args.Message;
            var sessionId = args.SessionId;

            WriteLine($"[BRIDGE-{label}] Received '{message.MessageId}' on session '{sessionId}'");

            await EnsureSessionLockBudgetAsync(args, ct, TransactionLockBudget);
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

        // These limiters are pump-scoped. Separate endpoint processes therefore get
        // independent limits, matching the competing-process topology.
        using var sessionConcurrency = new ConcurrencyLimiter(
            new ConcurrencyLimiterOptions { PermitLimit = 3, QueueLimit = 0 });
        using var acceptThrottle = new TokenBucketRateLimiter(
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = 3,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromMilliseconds(200),
                QueueLimit = int.MaxValue,
                AutoReplenishment = true
            });
        await using var inputQueueSender = client.CreateSender(Prepare.InputQueueName);
        var blockedSessionCooldown = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        var workers = new Task[AcceptWorkers];
        for (int i = 0; i < AcceptWorkers; i++)
        {
            var workerId = i + 1;
            workers[i] = RunPumpWorkerAsync(client, inputQueueSender, sessionConcurrency, acceptThrottle, blockedSessionCooldown, workerId, ct);
        }

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            WriteLine("[PUMP] Cancellation requested.");
        }

        WriteLine("[PUMP] Stopped");
    }

    static async Task RunPumpWorkerAsync(ServiceBusClient client, ServiceBusSender inputQueueSender, ConcurrencyLimiter sessionConcurrency, TokenBucketRateLimiter acceptThrottle, ConcurrentDictionary<string, DateTime> blockedSessionCooldown, int workerId, CancellationToken ct)
    {
        WriteLine($"[PUMP-{workerId}] Started");

        while (!ct.IsCancellationRequested)
        {
            ServiceBusSessionReceiver? sessionReceiver = null;
            RateLimitLease? concurrencyLease = null;

            try
            {
                concurrencyLease = await sessionConcurrency.AcquireAsync(1, ct);
                if (!concurrencyLease.IsAcquired)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                using var throttleLease = await acceptThrottle.AcquireAsync(1, ct);
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

                // Cooldown is checked only after accepting a session. It avoids repeated
                // scans and processing while a blocked retry is not due; it does not avoid
                // repeated AcceptNextSessionAsync calls.
                if (IsSessionInCooldown(blockedSessionCooldown, sessionId))
                {
                    await ReleaseSessionAsync(sessionReceiver, sessionId);
                    continue;
                }

                await ProcessSessionAsync(sessionReceiver, inputQueueSender, blockedSessionCooldown, sessionId, workerId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ServiceBusException ex)
                when (ex.Reason == ServiceBusFailureReason.ServiceTimeout
                   || ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                WriteLine($"[PUMP-{workerId}] Session accept timed out or could not be locked: {ex.Message}");
            }
            catch (Exception ex)
            {
                WriteLine($"[PUMP-{workerId}] Error: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (sessionReceiver != null)
                        await sessionReceiver.DisposeAsync();
                }
                finally
                {
                    concurrencyLease?.Dispose();
                }
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
    static async Task ProcessSessionAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, ConcurrentDictionary<string, DateTime> blockedSessionCooldown, string sessionId, int workerId, CancellationToken ct)
    {
        var sessionState = await ReadSessionStateAsync(receiver, ct);

        if (sessionState.IsBlocked)
        {
            await ProcessBlockedSessionAsync(receiver, inputQueueSender, blockedSessionCooldown, sessionId, sessionState, workerId, ct);
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
            var ok = await TryHandleAsync(receiver, inputQueueSender, blockedSessionCooldown, message, sessionId, workerId, clearsBlockedStateOnSuccess: false, ct);
            if (!ok) break; // a failure blocked the session; stop draining this batch
        }

        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // The persisted peek frontier skips backlog already ruled out. Physical retry
    // identity remains unambiguous even when logical identities repeat.
    static async Task ProcessBlockedSessionAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, ConcurrentDictionary<string, DateTime> blockedSessionCooldown, string sessionId, SessionState sessionState, int workerId, CancellationToken ct)
    {
        WriteLine($"[PUMP-{workerId}] Session '{sessionId}' BLOCKED on logical '{sessionState.LogicalMessageId}', waiting for '{sessionState.ExpectedRetryMessageId}'.");

        // Scheduled messages may be peeked while scheduled, but cannot be received
        // before their due time. This implementation intentionally waits until due
        // before scanning, so the cooldown avoids repeated scans and processing.
        if (sessionState.RetryAfter is DateTimeOffset retryAfter && retryAfter > DateTimeOffset.UtcNow)
        {
            blockedSessionCooldown[sessionId] = retryAfter.UtcDateTime;
            var wait = retryAfter - DateTimeOffset.UtcNow;
            WriteLine($"[PUMP-{workerId}]   Retry not due until +{(int)Math.Ceiling(wait.TotalSeconds)}s — cooldown. Releasing.");
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        // A scheduled message may be visible to peek while scheduled, but activation
        // appends it with a new final sequence number. The frontier contains only
        // active sequence numbers, so it cannot skip the activated retry.
        long? matchSeq = null;
        long? lastPeeked = null;
        {
            long? fromSeq = sessionState.LastPeekedSequenceNumber is long frontier
                ? frontier + 1
                : null;
            const int peekBatch = 32;
            while (!ct.IsCancellationRequested)
            {
                await EnsureSessionLockBudgetAsync(receiver, ct, SessionLockRenewalThreshold);
                var peeked = await receiver.PeekMessagesAsync(peekBatch, fromSeq, ct);
                if (peeked.Count == 0)
                    break; // session exhausted; retry is not active in the queue

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
                await EnsureSessionLockBudgetAsync(receiver, ct, OrdinaryCompletionLockBudget);
                await receiver.SetSessionStateAsync(
                    BinaryData.FromBytes(Encoding.UTF8.GetBytes(updated.ToJson())), ct);
                WriteLine($"[PUMP-{workerId}]   Persisted peek frontier at seq {lp}.");
            }
            blockedSessionCooldown[sessionId] = DateTime.UtcNow.AddSeconds(1);
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
            await EnsureSessionLockBudgetAsync(receiver, ct, SessionLockRenewalThreshold);
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
            blockedSessionCooldown[sessionId] = DateTime.UtcNow.AddSeconds(1);
            await ReleaseSessionAsync(receiver, sessionId, workerId);
            return;
        }

        // Process ONLY the retry. Everything else in the session — the locked backlog
        // ahead of it and any messages we received past it — re-flows on close and is
        // processed in original order on the next accept. Processing past the retry in
        // this accept would let post-retry messages jump ahead of the held-back backlog.
        await TryHandleAsync(receiver, inputQueueSender, blockedSessionCooldown, retryMessage, sessionId, workerId, clearsBlockedStateOnSuccess: true, ct);
        await ReleaseSessionAsync(receiver, sessionId, workerId);
    }

    // ---------------------------------------------------------------
    // SCENARIO ASSERTIONS
    // ---------------------------------------------------------------

    static ScenarioPlan CreateScenarioPlan(bool stretchBacklog)
    {
        var expected = new List<ScenarioExpectedCompletion>
        {
            new("Customer-123", "cust123-msg1", "cust123-msg1", 1),
            new("Customer-123", "cust123-msg2", CreatePhysicalMessageId("delayed", "Customer-123", "cust123-msg2", 1), 2)
        };

        if (stretchBacklog)
        {
            for (var i = 100; i < 140; i++)
                expected.Add(new("Customer-123", $"cust123-backlog-{i}", $"cust123-backlog-{i}", 1));
        }

        expected.AddRange(
        [
            new("Customer-123", "cust123-msg3", "cust123-msg3", 1),
            new("Customer-123", "cust123-msg5", "cust123-msg5", 1),
            new("Customer-123", "cust123-msg6", "cust123-msg6", 1),
            new("Customer-123", "cust123-msg7", "cust123-msg7", 1),
            new("Customer-456", "cust456-msg1", "cust456-msg1", 1),
            new("Customer-456", "cust456-msg2", "cust456-msg2", 1),
            new("Customer-789", "cust789-msg1", CreatePhysicalMessageId("delayed", "Customer-789", "cust789-msg1", 2), 3),
            new("Stock-001", "stock001-msg1", "stock001-msg1", 1),
            new("Stock-001", "stock001-msg2", "stock001-msg2", 1),
            new("Stock-002", "stock002-msg1", "stock002-msg1", 1)
        ]);

        return new ScenarioPlan(expected);
    }

    static ScenarioPlan CreateTerminalScenarioPlan() => new(
    [
        new("Terminal-001", TerminalScenarioLogicalMessageId, CreatePhysicalMessageId("manual", "Terminal-001", TerminalScenarioLogicalMessageId, 1), MaxAttempts)
        {
            RetryCount = MaxAttempts - 1,
            ManualRetryCount = 1
        },
        new("Terminal-001", "terminal-msg2", "terminal-msg2", 1),
        new("Terminal-001", "terminal-msg3", "terminal-msg3", 1)
    ]);

    static ScenarioPlan CreateRestartScenarioPlan() => new(
    [
        new("Customer-123", "cust123-msg1", "cust123-msg1", 1),
        new("Customer-123", "cust123-msg2", CreatePhysicalMessageId("delayed", "Customer-123", "cust123-msg2", 1), 2),
        new("Customer-123", "cust123-msg3", "cust123-msg3", 1),
        new("Customer-456", "cust456-msg1", "cust456-msg1", 1),
        new("Customer-456", "cust456-msg2", "cust456-msg2", 1),
        new("Customer-789", "cust789-msg1", CreatePhysicalMessageId("delayed", "Customer-789", "cust789-msg1", 2), 3),
        new("Stock-001", "stock001-msg1", "stock001-msg1", 1),
        new("Stock-001", "stock001-msg2", "stock001-msg2", 1),
        new("Stock-002", "stock002-msg1", "stock002-msg1", 1)
    ]);

    static async Task WaitForScenarioAsync(ScenarioPlan plan, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        string lastFailure = "no completions recorded";
        while (!timeoutCts.IsCancellationRequested)
        {
            var completions = ScenarioObserver.Snapshot();
            if (ScenarioObserver.TryValidate(plan, completions, out lastFailure))
            {
                var count = completions.Count;
                await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token);
                if (ScenarioObserver.Count != count)
                    throw new InvalidOperationException(
                        $"Scenario completion set was not quiescent; expected {count} records but observed {ScenarioObserver.Count}.");

                completions = ScenarioObserver.Snapshot();
                if (!ScenarioObserver.TryValidate(plan, completions, out lastFailure))
                    throw new InvalidOperationException(lastFailure);

                WriteLine($"[ASSERT] Scenario assertions passed for {completions.Count} settled handler completions.");
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out after {timeout} waiting for scenario assertions: {lastFailure}.");
    }

    static async Task WaitUntilAsync(DateTimeOffset dueAt, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (DateTimeOffset.UtcNow < dueAt)
        {
            var remaining = dueAt - DateTimeOffset.UtcNow;
            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                timeoutCts.Token);
        }
    }

    static async Task WaitForSessionMessagesAsync(ServiceBusClient client, string sessionId, int expectedCount, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (true)
        {
            ServiceBusSessionReceiver? receiver = null;
            try
            {
                receiver = await client.AcceptSessionAsync(
                    Prepare.InputQueueName,
                    sessionId,
                    new ServiceBusSessionReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.PeekLock,
                        PrefetchCount = 0
                    },
                    timeoutCts.Token);

                var messages = await receiver.PeekMessagesAsync(expectedCount, cancellationToken: timeoutCts.Token);
                if (messages.Count >= expectedCount)
                    return;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for {expectedCount} messages in session '{sessionId}'.");
            }
            catch (ServiceBusException ex)
                when (ex.Reason == ServiceBusFailureReason.ServiceTimeout
                   || ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                WriteLine($"[STRETCH] Session probe will retry: {ex.Message}");
            }
            finally
            {
                if (receiver != null)
                {
                    try
                    {
                        await receiver.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        WriteLine($"[STRETCH] Session probe disposal failed: {ex.Message}");
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for {expectedCount} messages in session '{sessionId}'.");
            }
        }
    }

    static async Task<SessionState> WaitForBlockedSessionAsync(ServiceBusClient client, string sessionId, TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (true)
        {
            ServiceBusSessionReceiver? receiver = null;
            try
            {
                receiver = await client.AcceptSessionAsync(
                    Prepare.InputQueueName,
                    sessionId,
                    new ServiceBusSessionReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.PeekLock,
                        PrefetchCount = 0
                    },
                    timeoutCts.Token);

                var state = await ReadSessionStateAsync(receiver, timeoutCts.Token);
                if (state.IsBlocked)
                    return state;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for session '{sessionId}' to become blocked.");
            }
            catch (ServiceBusException ex)
                when (ex.Reason == ServiceBusFailureReason.ServiceTimeout
                   || ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                WriteLine($"[RESTART] Session probe will retry: {ex.Message}");
            }
            finally
            {
                if (receiver != null)
                {
                    try
                    {
                        await receiver.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        WriteLine($"[RESTART] Session probe disposal failed: {ex.Message}");
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for session '{sessionId}' to become blocked.");
            }
        }
    }

    // ---------------------------------------------------------------
    // SESSION STATE HELPERS
    // ---------------------------------------------------------------

    static async Task EnsureSessionLockBudgetAsync(
        ServiceBusSessionReceiver receiver,
        CancellationToken ct,
        TimeSpan minimumRemaining)
    {
        var remaining = receiver.SessionLockedUntil - DateTimeOffset.UtcNow;
        if (remaining < minimumRemaining)
        {
            await receiver.RenewSessionLockAsync(ct);
            remaining = receiver.SessionLockedUntil - DateTimeOffset.UtcNow;
        }

        if (remaining < minimumRemaining)
        {
            throw new TimeoutException(
                $"Session '{receiver.SessionId}' has only {remaining.TotalSeconds:F1}s of lock budget remaining.");
        }
    }

    static async Task EnsureSessionLockBudgetAsync(
        ProcessSessionMessageEventArgs args,
        CancellationToken ct,
        TimeSpan minimumRemaining)
    {
        var remaining = args.SessionLockedUntil - DateTimeOffset.UtcNow;
        if (remaining < minimumRemaining)
        {
            await args.RenewSessionLockAsync(ct);
            remaining = args.SessionLockedUntil - DateTimeOffset.UtcNow;
        }

        if (remaining < minimumRemaining)
        {
            throw new TimeoutException(
                $"Session '{args.SessionId}' has only {remaining.TotalSeconds:F1}s of lock budget remaining.");
        }
    }

    static Task EnsureMessageLockBudgetAsync(ServiceBusReceivedMessage message, TimeSpan minimumRemaining)
    {
        var remaining = message.LockedUntil - DateTimeOffset.UtcNow;
        if (remaining < minimumRemaining)
        {
            throw new TimeoutException(
                $"Message '{message.MessageId}' has only {remaining.TotalSeconds:F1}s of lock budget remaining.");
        }

        return Task.CompletedTask;
    }

    static async Task<SessionState> ReadSessionStateAsync(ServiceBusSessionReceiver receiver, CancellationToken ct)
    {
        var binaryState = await receiver.GetSessionStateAsync(ct);
        return binaryState == null
            ? SessionState.Default
            : SessionState.FromJson(Encoding.UTF8.GetString(binaryState));
    }

    static async Task SetSessionBlockedStateAsync(ServiceBusSessionReceiver receiver, string logicalMessageId, string expectedRetryMessageId, DateTimeOffset retryAfter, int manualRetryCount, CancellationToken ct)
    {
        var state = new SessionState
        {
            IsBlocked = true,
            LogicalMessageId = logicalMessageId,
            ExpectedRetryMessageId = expectedRetryMessageId,
            ManualRetryCount = manualRetryCount,
            BlockedAt = DateTimeOffset.UtcNow,
            RetryAfter = retryAfter
        };

        await receiver.SetSessionStateAsync(BinaryData.FromBytes(Encoding.UTF8.GetBytes(state.ToJson())), ct);
    }

    static async Task<bool> TryHandleAsync(ServiceBusSessionReceiver receiver, ServiceBusSender inputQueueSender, ConcurrentDictionary<string, DateTime> blockedSessionCooldown, ServiceBusReceivedMessage message, string sessionId, int workerId, bool clearsBlockedStateOnSuccess, CancellationToken ct)
    {
        var logicalMessageId = GetLogicalMessageId(message);
        var attempt = GetCount(message, RetryCountProperty) + 1;
        var body = Encoding.UTF8.GetString(message.Body);
        WriteLine($"[PUMP-{workerId}]   Processing logical '{logicalMessageId}' via '{message.MessageId}' (attempt {attempt}, session '{sessionId}'): {body}");

        try
        {
            if (ShouldSimulateFailure(message, logicalMessageId, attempt))
                throw new InvalidOperationException($"Simulated failure #{attempt} for '{logicalMessageId}'");

            if (clearsBlockedStateOnSuccess)
            {
                await EnsureSessionLockBudgetAsync(receiver, ct, TransactionLockBudget);
                using (var transaction = CreateServiceBusTransaction())
                {
                    await receiver.CompleteMessageAsync(message, ct);
                    await receiver.SetSessionStateAsync(null as BinaryData, ct);
                    transaction.Complete();
                }

                blockedSessionCooldown.TryRemove(sessionId, out _);
                WriteLine($"[PUMP-{workerId}]   Completed logical '{logicalMessageId}' via '{message.MessageId}' and unblocked session '{sessionId}'");
            }
            else
            {
                await EnsureSessionLockBudgetAsync(receiver, ct, OrdinaryCompletionLockBudget);
                await receiver.CompleteMessageAsync(message, ct);
                ScenarioObserver.Record(message, sessionId, logicalMessageId, attempt);
                WriteLine($"[PUMP-{workerId}]   Completed logical '{logicalMessageId}' via '{message.MessageId}'");
            }

            if (clearsBlockedStateOnSuccess)
                ScenarioObserver.Record(message, sessionId, logicalMessageId, attempt);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteLine($"[PUMP-{workerId}]   FAILED logical '{logicalMessageId}' via '{message.MessageId}': {ex.Message}");

            if (attempt >= MaxAttempts)
            {
                var manualRetryCount = GetCount(message, ManualRetryCountProperty) + 1;
                var expectedRetryMessageId = CreatePhysicalMessageId("manual", sessionId, logicalMessageId, manualRetryCount);
                await EnsureSessionLockBudgetAsync(receiver, ct, TransactionLockBudget);
                using (var transaction = CreateServiceBusTransaction())
                {
                    await receiver.DeadLetterMessageAsync(message, deadLetterReason: "MaxRetriesExceeded", deadLetterErrorDescription: ex.Message, cancellationToken: ct);
                    await SetSessionBlockedStateAsync(
                        receiver,
                        logicalMessageId,
                        expectedRetryMessageId,
                        DateTimeOffset.UtcNow,
                        manualRetryCount,
                        ct);
                    transaction.Complete();
                }

                WriteLine($"[PUMP-{workerId}]   Terminal failure '{logicalMessageId}' -> DLQ (attempt {attempt} >= {MaxAttempts}). Session remains BLOCKED for manual '{expectedRetryMessageId}'; backlog remains held.");
                return false;
            }

            var retryMessageId = CreatePhysicalMessageId("delayed", sessionId, logicalMessageId, attempt);
            var resend = new ServiceBusMessage(message)
            {
                MessageId = retryMessageId,
                SessionId = sessionId
            };
            resend.ApplicationProperties[LogicalMessageIdProperty] = logicalMessageId;
            resend.ApplicationProperties[RetryCountProperty] = attempt;

            var scheduledEnqueueTime = DateTimeOffset.UtcNow + RetryDelay;
            await EnsureSessionLockBudgetAsync(receiver, ct, TransactionLockBudget);
            using (var transaction = CreateServiceBusTransaction())
            {
                await inputQueueSender.ScheduleMessageAsync(resend, scheduledEnqueueTime, ct);
                await SetSessionBlockedStateAsync(
                    receiver,
                    logicalMessageId,
                    retryMessageId,
                    scheduledEnqueueTime,
                    GetCount(message, ManualRetryCountProperty),
                    ct);
                await receiver.CompleteMessageAsync(message, ct);
                transaction.Complete();
            }

            WriteLine($"[PUMP-{workerId}]   Scheduled '{retryMessageId}' for logical '{logicalMessageId}' (+{(int)RetryDelay.TotalSeconds}s), original completed, session BLOCKED.");
            return false;
        }
    }

    static bool ShouldSimulateFailure(ServiceBusReceivedMessage message, string logicalMessageId, int attempt)
    {
        if (message.ApplicationProperties.TryGetValue(TerminalScenarioProperty, out var terminalValue)
            && terminalValue is bool isTerminalScenario
            && isTerminalScenario)
        {
            // The first recovery cycle is identified by durable metadata on the
            // message. A manual bring-back therefore succeeds without a process-local
            // exception or a transport-specific memory switch.
            return GetCount(message, ManualRetryCountProperty) == 0 && attempt <= MaxAttempts;
        }

        return FailureBudget.TryGetValue(logicalMessageId, out var budget) && attempt <= budget;
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

    static int GetCount(ServiceBusMessage message, string propertyName)
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

    static TransactionScope CreateServiceBusTransaction() =>
        new(
            TransactionScopeOption.RequiresNew,
            new TransactionOptions
            {
                IsolationLevel = IsolationLevel.Serializable,
                Timeout = ServiceBusTransactionTimeout
            },
            TransactionScopeAsyncFlowOption.Enabled);

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

    static bool IsSessionInCooldown(ConcurrentDictionary<string, DateTime> blockedSessionCooldown, string sessionId)
    {
        if (blockedSessionCooldown.TryGetValue(sessionId, out var cooldownUntil))
        {
            if (DateTime.UtcNow < cooldownUntil)
                return true;
            blockedSessionCooldown.TryRemove(sessionId, out _);
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
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            WriteLine("[DLQ] Startup cancelled before the processor began.");
            return;
        }

        WriteLine("[DLQ] Starting DLQ retry processor...");

        var deadLetterQueuePath = $"{Prepare.InputQueueName}/$DeadLetterQueue";
        // Azure Service Bus does not support session receivers on a dead-letter
        // subqueue. The input queue's session state remains the ordering authority;
        // this processor only transfers the dead-lettered message back atomically.
        var dlqProcessor = client.CreateProcessor(
            deadLetterQueuePath,
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1,
                PrefetchCount = 0,
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

        await using var sender = client.CreateSender(Prepare.InputQueueName);
        dlqProcessor.ProcessMessageAsync += async args =>
        {
            var message = args.Message;
            WriteLine($"[DLQ] Found dead-lettered message '{message.MessageId}'");
            WriteLine($"[DLQ]   DeadLetterReason: {message.DeadLetterReason}");
            WriteLine($"[DLQ]   DeadLetterErrorDescription: {message.DeadLetterErrorDescription}");

            var logicalMessageId = GetLogicalMessageId(message);
            var manualRetryCount = GetCount(message, ManualRetryCountProperty) + 1;
            if (manualRetryCount > MaxManualRetries)
            {
                // Leave the DLQ delivery locked and unsettled. There is no implicit
                // discard/unblock path: operational evidence and the durable session
                // block remain until an explicit operator action exists.
                WriteLine($"[DLQ] Manual retry limit exhausted for logical '{logicalMessageId}' ({manualRetryCount}). Leaving DLQ message unsettled and session blocked; explicit discard/unblock is required.");
                return;
            }

            var retryMessageId = CreatePhysicalMessageId("manual", message.SessionId!, logicalMessageId, manualRetryCount);
            var retryMessage = new ServiceBusMessage(message)
            {
                MessageId = retryMessageId,
                SessionId = message.SessionId
            };
            // The clone preserves body, application metadata, content type, and the
            // durable automated retry count. Only the physical identity and durable
            // manual-retry identity change for the new transport delivery.
            retryMessage.ApplicationProperties[LogicalMessageIdProperty] = logicalMessageId;
            retryMessage.ApplicationProperties[ManualRetryCountProperty] = manualRetryCount;

            WriteLine($"[DLQ] Manual retry #{manualRetryCount} for logical '{logicalMessageId}' as '{retryMessage.MessageId}' (RetryCount={GetCount(retryMessage, RetryCountProperty)})");

            await EnsureMessageLockBudgetAsync(args.Message, TransactionLockBudget);
            using (var transaction = CreateServiceBusTransaction())
            {
                // This is a cross-entity transaction: receive/complete the input
                // queue DLQ message and send its manual copy to the input queue
                // atomically using a client with EnableCrossEntityTransactions.
                await sender.SendMessageAsync(retryMessage, ct);
                await args.CompleteMessageAsync(message, ct);
                transaction.Complete();
            }

            WriteLine($"[DLQ] Atomically re-sent logical '{logicalMessageId}' to input queue (session '{message.SessionId}') and completed DLQ message '{message.MessageId}'");
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

        var m2 = new ServiceBusMessage("Payment processing for Customer-123")
        { MessageId = "cust123-msg2", SessionId = "Customer-123" };
        await salesSender.SendMessageAsync(m2);
        WriteLine($"  Published '{m2.MessageId}' (session: Customer-123) [WILL FAIL]");

        // Stretch mode fills the session after msg2 but starts the pump only after the
        // bridge has forwarded the batch. The scheduled retry then lands past one peek
        // page, behind this unsettled backlog.
        if (stretchBacklog)
        {
            WriteLine("  [STRETCH] Publishing 40 backlog messages behind Customer-123 msg2...");
            for (int i = 100; i < 140; i++)
            {
                var bm = new ServiceBusMessage($"Backlog filler #{i - 99} for Customer-123")
                { MessageId = $"cust123-backlog-{i}", SessionId = "Customer-123" };
                await salesSender.SendMessageAsync(bm);
            }
            WriteLine("  [STRETCH] 40 backlog messages published.");
        }

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

internal sealed record ScenarioExpectedCompletion(
    string SessionId,
    string LogicalMessageId,
    string PhysicalMessageId,
    int Attempt)
{
    public int? RetryCount { get; init; }
    public int? ManualRetryCount { get; init; }
}

internal sealed record ScenarioPlan(IReadOnlyList<ScenarioExpectedCompletion> Expected);

internal sealed class ScenarioCompletionObserver
{
    readonly ConcurrentQueue<ScenarioExpectedCompletion> completions = new();

    public int Count => completions.Count;

    public void Clear()
    {
        while (completions.TryDequeue(out _))
        {
        }
    }

    public void Record(ServiceBusReceivedMessage message, string sessionId, string logicalMessageId, int attempt) =>
        completions.Enqueue(new ScenarioExpectedCompletion(sessionId, logicalMessageId, message.MessageId, attempt)
        {
            RetryCount = GetMessageCount(message, Program.RetryCountProperty),
            ManualRetryCount = GetMessageCount(message, Program.ManualRetryCountProperty)
        });

    static int GetMessageCount(ServiceBusReceivedMessage message, string propertyName) =>
        message.ApplicationProperties.TryGetValue(propertyName, out var value)
            ? value switch
            {
                int count => count,
                long count when count is >= 0 and <= int.MaxValue => (int)count,
                _ => 0
            }
            : 0;

    public IReadOnlyList<ScenarioExpectedCompletion> Snapshot() => completions.ToArray();

    public bool TryValidate(ScenarioPlan plan, IReadOnlyList<ScenarioExpectedCompletion> actual, out string failure)
    {
        var expectedBySession = plan.Expected.GroupBy(completion => completion.SessionId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var actualBySession = actual.GroupBy(completion => completion.SessionId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (actual.Count != plan.Expected.Count)
        {
            failure = $"Expected {plan.Expected.Count} settled completions, observed {actual.Count}.";
            return false;
        }

        foreach (var unexpectedSession in actualBySession.Keys.Except(expectedBySession.Keys, StringComparer.Ordinal))
        {
            failure = $"Observed an unexpected session completion for '{unexpectedSession}'.";
            return false;
        }

        foreach (var (sessionId, expected) in expectedBySession)
        {
            if (!actualBySession.TryGetValue(sessionId, out var observed) || observed.Length != expected.Length)
            {
                failure = $"Session '{sessionId}' expected {expected.Length} logical completions, observed {observed?.Length ?? 0}.";
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                var wanted = expected[i];
                var got = observed[i];
                if (wanted.SessionId != got.SessionId
                    || wanted.LogicalMessageId != got.LogicalMessageId
                    || wanted.PhysicalMessageId != got.PhysicalMessageId
                    || wanted.Attempt != got.Attempt)
                {
                    failure = $"Session '{sessionId}' completion {i + 1} expected " +
                        $"{wanted.LogicalMessageId}/{wanted.PhysicalMessageId}/attempt{wanted.Attempt}, " +
                        $"observed {got.LogicalMessageId}/{got.PhysicalMessageId}/attempt{got.Attempt}.";
                    return false;
                }

                if (wanted.RetryCount is int expectedRetryCount && got.RetryCount != expectedRetryCount)
                {
                    failure = $"Session '{sessionId}' completion {i + 1} expected durable RetryCount {expectedRetryCount}, observed {got.RetryCount}.";
                    return false;
                }

                if (wanted.ManualRetryCount is int expectedManualRetryCount && got.ManualRetryCount != expectedManualRetryCount)
                {
                    failure = $"Session '{sessionId}' completion {i + 1} expected durable ManualRetryCount {expectedManualRetryCount}, observed {got.ManualRetryCount}.";
                    return false;
                }
            }
        }

        failure = string.Empty;
        return true;
    }
}

// ---------------------------------------------------------------
// SESSION STATE (versioned JSON envelope)
// ---------------------------------------------------------------
//
// This spike intentionally models only the transport section. It is not an
// implementation of the public session-state API and does not preserve user-owned
// state. Production code must preserve a separate user section while updating this
// transport section; that remains an open production item.

public record SessionState
{
    public bool IsBlocked { get; init; }
    public string? LogicalMessageId { get; init; }
    public string? ExpectedRetryMessageId { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }
    public int ManualRetryCount { get; init; }
    public long? LastPeekedSequenceNumber { get; init; }

    public static SessionState Default => new() { IsBlocked = false };

    public string ToJson()
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 7,
            transport = new
            {
                blocked = IsBlocked,
                logicalMessageId = LogicalMessageId,
                expectedRetryMessageId = ExpectedRetryMessageId,
                blockedAt = BlockedAt?.ToString("O"),
                retryAfter = RetryAfter?.ToString("O"),
                manualRetryCount = ManualRetryCount,
                lastPeekedSequenceNumber = LastPeekedSequenceNumber
            }
        });
    }

    public static SessionState FromJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.GetProperty("version").GetInt32();
        if (version is not (6 or 7))
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
            ManualRetryCount = version >= 7
                && transport.TryGetProperty("manualRetryCount", out var manualRetryCount)
                && manualRetryCount.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? manualRetryCount.GetInt32()
                    : 0,
            LastPeekedSequenceNumber = transport.TryGetProperty("lastPeekedSequenceNumber", out var lastPeeked) && lastPeeked.ValueKind == System.Text.Json.JsonValueKind.Number
                ? lastPeeked.GetInt64()
                : null
        };
    }
}
