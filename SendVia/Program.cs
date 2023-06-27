using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Transactions;
using static System.Console;
using static System.Text.Encoding;

namespace SendVia
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string inputQueue = "repro-queue";
        private static readonly string destinationQueue = "repro-destination";
        private static readonly string errorQueue = "repro-error";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
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
                EnableCrossEntityTransactions = true,
                // RetryOptions = new ServiceBusRetryOptions
                // {
                //     TryTimeout = TimeSpan.FromSeconds(2)
                // }
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

                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                await Prepare.ReportNumberOfMessages(connectionString, errorQueue);

                try
                {
                    using var receiveTransaction = new CommittableTransaction(new TransactionOptions()
                    {
                        IsolationLevel = IsolationLevel.Serializable, Timeout = TransactionManager.MaximumTimeout
                    });
                    int numberOfMessages = 105;
                    var tasks = new List<Task>(numberOfMessages);

                    for (int i = 0; i < numberOfMessages; i++)
                    {
                        async Task SendMessage(Transaction transaction, int value)
                        {
                            using var scope = new TransactionScope(transaction,
                                TransactionScopeAsyncFlowOption.Enabled);
                            await sender.SendMessageAsync(new ServiceBusMessage(value.ToString()));
                            scope.Complete();
                        }

                        tasks.Add(SendMessage(receiveTransaction, i));
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
                        await errorQueueSender.SendMessageAsync(new ServiceBusMessage("error"));
                        await processMessageEventArgs.CompleteMessageAsync(message);
                        scope.Complete();

                        // scope needs to be disposed here since actual transaction handover happens on dispose. Yeah I know, silly
                    }

                    errorQueueTransaction.Commit();
                }
                finally
                {
                    await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                    await Prepare.ReportNumberOfMessages(connectionString, errorQueue);
                }
            };
            receiver.ProcessErrorAsync += async e =>
            {
                WriteLine(e.Exception.Message);
                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                await Prepare.ReportNumberOfMessages(connectionString, errorQueue);
            };

            await receiver.StartProcessingAsync();

            ReadLine();

            await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
            await Prepare.ReportNumberOfMessages(connectionString, errorQueue);

            await receiver.StopProcessingAsync();
            await receiver.CloseAsync();
            await sender.CloseAsync();
        }
    }
}