using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Transactions;
using static System.Console;
using static System.Text.Encoding;

namespace SendVia
{
    using System.Diagnostics.Tracing;
    using Azure.Core.Diagnostics;
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string inputQueue = "repro-batched-queue";
        private static readonly string destinationQueue = "repro-batched-destination";
        private static readonly string errorQueue = "repro-batched-error";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            using AzureEventSourceListener listener = new AzureEventSourceListener((args, message) =>
            {
                if(args.EventSource.Name == "Azure-Messaging-ServiceBus" && args.EventName.Contains("Transaction"))
                {
                    WriteLine("==================");
                    WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss:fff} EVENT: {args.EventName}");
                    WriteLine(message);
                    if (args.PayloadNames != null && args.Payload != null)
                    {
                        for (var i = 0; i < args.PayloadNames.Count; i++)
                        {
                            WriteLine($"\t{args.PayloadNames[i]}: {args.Payload[i]}");
                        }
                    }
                    WriteLine("==================");
                }
            }, EventLevel.LogAlways);

            await Prepare.Stage(connectionString, inputQueue, destinationQueue, errorQueue);

            await using var serviceBusClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });
            await using var kickOffSender = serviceBusClient.CreateSender(inputQueue);

            var winMessage = new ServiceBusMessage("Win");
            await kickOffSender.SendMessageAsync(winMessage);
            await kickOffSender.CloseAsync();

            await using var transactionalClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                EnableCrossEntityTransactions = true
            });
            var receiver = transactionalClient.CreateProcessor(inputQueue, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false
            });
            var sender = transactionalClient.CreateSender(destinationQueue);
            var errorQueueSender = transactionalClient.CreateSender(errorQueue);

            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;

                WriteLine("Counts on message receive:");
                await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                await Prepare.ReportNumberOfMessages(connectionString, errorQueue);

                try
                {
                    using var receiveTransaction = new CommittableTransaction(new TransactionOptions()
                    {
                        IsolationLevel = IsolationLevel.Serializable, Timeout = TransactionManager.MaximumTimeout
                    });

                    int numberOfMessages = 1000;
                    var messagesToSend = new Queue<ServiceBusMessage>(numberOfMessages);
                    for (int i = 0; i < numberOfMessages; i++)
                    {
                        messagesToSend.Enqueue(new ServiceBusMessage(i.ToString()));
                    }

                    int batchCount = 0;
                    int maxItemsPerBatch = 100;
                    var tasks = new List<Task>(numberOfMessages);
                    while (messagesToSend.Count > 0)
                    {
                        using ServiceBusMessageBatch messageBatch = await sender.CreateMessageBatchAsync()
                            .ConfigureAwait(false);

                        while (messagesToSend.Count > 0 && messageBatch.Count < maxItemsPerBatch && messageBatch.TryAddMessage(messagesToSend.Peek()))
                        {
                            messagesToSend.Dequeue();
                        }

                        batchCount++;

                        using var scope =  new TransactionScope(receiveTransaction, TransactionScopeAsyncFlowOption.Enabled);
                        WriteLine($"Sending batch {batchCount + 1}");
                        await sender.SendMessagesAsync(messageBatch).ConfigureAwait(false);
                        scope.Complete();
                    }

                    await Task.WhenAll(tasks);

                    receiveTransaction.Commit();
                }
                catch (Exception e)
                {
                    WriteLine(e.Message);
                    using var errorQueueTransaction = new CommittableTransaction(new TransactionOptions()
                    {
                        IsolationLevel = IsolationLevel.Serializable, Timeout = TransactionManager.MaximumTimeout
                    });
                    using (var scope = new TransactionScope(errorQueueTransaction, TransactionScopeAsyncFlowOption.Enabled))
                    {
                        await errorQueueSender.SendMessageAsync(new ServiceBusMessage(message));
                        await processMessageEventArgs.CompleteMessageAsync(message);
                        scope.Complete();

                        // scope needs to be disposed here since actual transaction handover happens on dispose. Yeah I know, silly
                    }

                    errorQueueTransaction.Commit();
                }
                finally
                {
                    WriteLine("Counts in finally block");
                    await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
                    await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                    await Prepare.ReportNumberOfMessages(connectionString, errorQueue);
                }
            };
            receiver.ProcessErrorAsync += async e =>
            {
                WriteLine(e.Exception.Message);
                await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                await Prepare.ReportNumberOfMessages(connectionString, errorQueue);
            };

            await receiver.StartProcessingAsync();

            ReadLine();

            WriteLine("Counts on process exit");
            await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
            await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
            await Prepare.ReportNumberOfMessages(connectionString, errorQueue);

            await receiver.StopProcessingAsync();
            await receiver.CloseAsync();
            await sender.CloseAsync();
        }
    }
}
