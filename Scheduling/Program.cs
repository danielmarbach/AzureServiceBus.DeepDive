using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Scheduling
{
    using System.Threading;
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

            var due = DateTimeOffset.UtcNow.AddSeconds(10);
            await sender.ScheduleMessageAsync(new ServiceBusMessage($"Deep Dive + {due}"), due);
            Console.WriteLine($"{DateTimeOffset.UtcNow}: Message scheduled first");

            var sequenceId =
                await sender.ScheduleMessageAsync(new ServiceBusMessage($"Deep Dive + {due}"), due);
            Console.WriteLine($"{DateTimeOffset.UtcNow}: Message scheduled second");

            await sender.CancelScheduledMessageAsync(sequenceId);
            Console.WriteLine($"{DateTimeOffset.UtcNow}: Canceled second");

            await using var receiver = serviceBusClient.CreateProcessor(destination, new ServiceBusProcessorOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });
            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;

                await Console.Error.WriteLineAsync(
                    $"{DateTimeOffset.UtcNow}: Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");

                syncEvent.TrySetResult(true);
            };
            receiver.ProcessErrorAsync += _ => Task.CompletedTask;

            await receiver.StartProcessingAsync();

            await syncEvent.Task;

            await receiver.StopProcessingAsync();
        }
    }
}