using System;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace TransferDLQ
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string inputQueue = "queue";
        private static readonly string destinationQueue = "destination";

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
            await kickOffSender.SendMessageAsync(new ServiceBusMessage("Kick off"));

            await using var transactionalClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                EnableCrossEntityTransactions = true,
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });


            var receiver = transactionalClient.CreateProcessor(inputQueue);
            var sender = transactionalClient.CreateSender(destinationQueue);

            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;

                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    Console.WriteLine(
                        $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                    await sender.SendMessageAsync(new ServiceBusMessage("Will not leak"));

                    await Prepare.Hazard(connectionString, destinationQueue);

                    scope.Complete();
                }

                await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
            };
            receiver.ProcessErrorAsync += _ => Prepare.ReportNumberOfMessages(connectionString, destinationQueue);

            await receiver.StartProcessingAsync();

            Console.ReadLine();

            await Prepare.ReportNumberOfMessages(connectionString, inputQueue);

            await receiver.StopProcessingAsync();
        }
    }
}