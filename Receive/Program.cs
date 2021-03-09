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
            receiver.ProcessMessageAsync += async messageEventArgs => 
            {
                var message = messageEventArgs.Message;
                await Console.Out.WriteLineAsync(
                    $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                // throw new InvalidOperationException();
                await messageEventArgs.CompleteMessageAsync(message);
                syncEvent.TrySetResult(true);
            };
            receiver.ProcessErrorAsync += async errorEventArgs =>
            {
                await Console.Out.WriteLineAsync($"Exception: {errorEventArgs.Exception}");
                await Console.Out.WriteLineAsync($"FullyQualifiedNamespace: {errorEventArgs.FullyQualifiedNamespace}");
                await Console.Out.WriteLineAsync($"ErrorSource: {errorEventArgs.ErrorSource}");
                await Console.Out.WriteLineAsync($"EntityPath: {errorEventArgs.EntityPath}");
            };

            await receiver.StartProcessingAsync();

            await syncEvent.Task;

            await receiver.StopProcessingAsync();
        }
    }
}