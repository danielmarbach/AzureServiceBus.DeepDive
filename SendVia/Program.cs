using System;
using System.Linq;
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
        private static readonly string anotherDestinationQueue = "anotherdestination";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        private static TaskCompletionSource<bool> syncEvent2 =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, destinationQueue, anotherDestinationQueue);

            await using var nonTransactionalClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });
            
            await using var transactionalClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                EnableCrossEntityTransactions = true,
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });
            
            await using var kickOffSender = nonTransactionalClient.CreateSender(inputQueue);
            await kickOffSender.SendMessageAsync(new ServiceBusMessage("Fail"));
            var winMessage = new ServiceBusMessage("Win");
            winMessage.ApplicationProperties.Add("Win", true);
            await kickOffSender.SendMessageAsync(winMessage);
            await kickOffSender.CloseAsync();
            
            var receiver = transactionalClient.CreateProcessor(inputQueue);
            var sender = transactionalClient.CreateSender(destinationQueue);
            var anotherSender = transactionalClient.CreateSender(anotherDestinationQueue);

            await anotherSender.SendMessagesAsync(Enumerable.Range(0, 10).Select(i => new ServiceBusMessage(i.ToString())));

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

                    await Prepare.ReportNumberOfMessages(connectionString, destinationQueue, anotherDestinationQueue);
                    
                    syncEvent.SetResult(true);
                    
                    await syncEvent2.Task;

                    scope.Complete();
                }

                await Prepare.ReportNumberOfMessages(connectionString, destinationQueue, anotherDestinationQueue);
            };
            receiver.ProcessErrorAsync += _ => Prepare.ReportNumberOfMessages(connectionString, destinationQueue, anotherDestinationQueue);
            
            await receiver.StartProcessingAsync();

            await syncEvent.Task;
            
            using (var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
            {
                await anotherSender.SendMessagesAsync(Enumerable.Range(11, 10).Select(i => new ServiceBusMessage(i.ToString())));
                
                scope.Complete();
            }
            
            syncEvent2.SetResult(true);

            ReadLine();

            await receiver.StopProcessingAsync();
            await receiver.CloseAsync();
            await sender.CloseAsync();
        }
    }
}