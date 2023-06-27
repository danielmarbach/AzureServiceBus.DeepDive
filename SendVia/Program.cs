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

        private static readonly string inputQueue = "queue";
        private static readonly string destinationQueue = "destination";
        private static readonly string errorQueue = "error";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, destinationQueue);

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
            var receiver = transactionalClient.CreateProcessor(inputQueue);
            var sender = transactionalClient.CreateSender(destinationQueue);
            var errorQueueSender = transactionalClient.CreateSender(errorQueue);

            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;

                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                await Prepare.ReportNumberOfMessages(connectionString, errorQueue);

                try
                {
                    using var transaction = new CommittableTransaction(new TransactionOptions()
                        { IsolationLevel = IsolationLevel.Serializable, Timeout = TransactionManager.MaximumTimeout });
                    int numberOfMessages = 350;
                    var tasks = new List<Task>(numberOfMessages);

                    for (int i = 0; i < numberOfMessages; i++)
                    {
                        void WrongUsage()
                        {
                            using var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled);
                            tasks.Add(sender.SendMessageAsync(new ServiceBusMessage(i.ToString())));
                            scope.Complete();
                        }

                        async Task CorrectUsage()
                        {
                            using var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled);
                            await sender.SendMessageAsync(new ServiceBusMessage(i.ToString()));
                            scope.Complete();
                        }

                        //WrongUsage();
                        tasks.Add(CorrectUsage());
                    }

                    await Task.WhenAll(tasks);

                    transaction.Commit();
                }
                catch (Exception e)
                {
                    WriteLine(e.Message);
                    using var transaction = new CommittableTransaction(new TransactionOptions()
                        { IsolationLevel = IsolationLevel.Serializable, Timeout = TransactionManager.MaximumTimeout });
                    using var scope = new TransactionScope(transaction, TransactionScopeAsyncFlowOption.Enabled);

                    await errorQueueSender.SendMessageAsync(new ServiceBusMessage(message));

                    scope.Complete();
                    transaction.Commit();

                    await processMessageEventArgs.CompleteMessageAsync(message);
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