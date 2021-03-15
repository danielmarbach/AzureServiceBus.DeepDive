using System;
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

            var receiver = serviceBusClient.CreateProcessor(inputQueue);
            var sender = serviceBusClient.CreateSender(destinationQueue);

            await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);

            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;

                await Error.WriteLineAsync(
                    $"Received message with '{message.MessageId}' and content '{UTF8.GetString(message.Body)}'");
                await sender.SendMessageAsync(new ServiceBusMessage("Will leak"));
                throw new InvalidOperationException();
            };
            receiver.ProcessErrorAsync += _ => Prepare.ReportNumberOfMessages(connectionString, destinationQueue);

            await receiver.StartProcessingAsync();

            ReadLine();
            await receiver.StopProcessingAsync();
            await receiver.CloseAsync();
            await sender.CloseAsync();

            await Prepare.Stage(connectionString, inputQueue, destinationQueue);

            await kickOffSender.SendMessageAsync(new ServiceBusMessage("Fail"));
            var winMessage = new ServiceBusMessage("Win");
            winMessage.ApplicationProperties.Add("Win", true);
            await kickOffSender.SendMessageAsync(winMessage);
            await kickOffSender.CloseAsync();

            await using var transactionalClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                EnableCrossEntityTransactions = true,
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });
            receiver = transactionalClient.CreateProcessor(inputQueue);
            sender = transactionalClient.CreateSender(destinationQueue);

            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;
                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    WriteLine(
                        $"Received message with '{message.MessageId}' and content '{UTF8.GetString(message.Body)}'");
                    await sender.SendMessageAsync(new ServiceBusMessage("Will not leak"));

                    if (!message.ApplicationProperties.ContainsKey("Win")) throw new InvalidOperationException();

                    await sender.SendMessageAsync(new ServiceBusMessage("Will not leak"));

                    scope.Complete();
                }

                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
            };
            receiver.ProcessErrorAsync += _ => Prepare.ReportNumberOfMessages(connectionString, destinationQueue);

            await receiver.StartProcessingAsync();

            ReadLine();

            await receiver.StopProcessingAsync();
            await receiver.CloseAsync();
            await sender.CloseAsync();
        }
    }
}