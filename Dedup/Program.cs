using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Dedup
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });

            await using var sender = serviceBusClient.CreateSender(destination);

            var content = Encoding.UTF8.GetBytes("Message1Message1");
            var messageId = new Guid(content).ToString();

            var messages = new List<ServiceBusMessage>
            {
                new ServiceBusMessage(content) { MessageId = messageId },
                new ServiceBusMessage(content) { MessageId = messageId },
                new ServiceBusMessage(content) { MessageId = messageId }
            };

            await sender.SendMessagesAsync(messages);

            Console.WriteLine("Messages sent");

            await using var receiver = serviceBusClient.CreateProcessor(destination, new ServiceBusProcessorOptions { AutoCompleteMessages = true, MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10) });

            receiver.ProcessMessageAsync += processMessagesEventArgs =>
            {
                var message = processMessagesEventArgs.Message;

                return Console.Error.WriteLineAsync(
                    $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
            };
            receiver.ProcessErrorAsync += processErrorEventArgs =>
                Console.Error.WriteLineAsync($"Exception: {processErrorEventArgs.Exception}");

            await receiver.StartProcessingAsync();

            await Task.Delay(TimeSpan.FromSeconds(25));

            await sender.SendMessagesAsync(messages);
            Console.WriteLine("Messages sent");

            Console.ReadLine();

            await receiver.CloseAsync();
        }
    }
}