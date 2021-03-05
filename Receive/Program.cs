using System;
using System.Text;
using System.Threading.Tasks;

namespace Receive
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static readonly TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await using var stage = await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });

            await using var sender = serviceBusClient.CreateSender(destination);
            await sender.SendMessageAsync(new ServiceBusMessage(Encoding.UTF8.GetBytes("Deep Dive")));
            Console.WriteLine("Message sent");

            var processorOptions = new ServiceBusProcessorOptions {
                    AutoCompleteMessages  = false,
                    MaxConcurrentCalls = 1,
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10)
            };

            await using var receiver = serviceBusClient.CreateProcessor(destination, processorOptions);

            async Task MessageHandler(ProcessMessageEventArgs args)
            {
                Console.WriteLine(
                    $"Received message with '{args.Message.MessageId}' and content '{Encoding.UTF8.GetString(args.Message.Body)}'");
                // throw new InvalidOperationException();
                await args.CompleteMessageAsync(args.Message);
                syncEvent.TrySetResult(true);
            }

            Task ErrorHandler(ProcessErrorEventArgs args)
            {
                Console.WriteLine($"Exception: {args.Exception}");
                Console.WriteLine($"FullyQualifiedNamespace: {args.FullyQualifiedNamespace}");
                Console.WriteLine($"ErrorSource: {args.ErrorSource}");
                Console.WriteLine($"EntityPath: {args.EntityPath}");
                return Task.CompletedTask;
            }

            receiver.ProcessMessageAsync += MessageHandler;
            receiver.ProcessErrorAsync += ErrorHandler;

            await receiver.StartProcessingAsync();

            await syncEvent.Task;

            await receiver.StopProcessingAsync();
        }
    }
}