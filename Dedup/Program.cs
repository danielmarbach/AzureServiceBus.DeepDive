using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Dedup
{
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

            var client = new QueueClient(connectionString, destination);
            try
            {
                var content = Encoding.UTF8.GetBytes("Message1Message1");
                var messageId = new Guid(content).ToString();

                var messages = new List<Message>
                {
                    new Message(content) { MessageId = messageId },
                    new Message(content) { MessageId = messageId },
                    new Message(content) { MessageId = messageId }
                };

                await client.SendAsync(messages);

                Console.WriteLine("Messages sent");

                client.RegisterMessageHandler(
                    (message, token) =>
                    {
                        Console.WriteLine(
                            $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                        return Task.CompletedTask;
                    },
                    new MessageHandlerOptions(
                        exception =>
                        {
                            Console.WriteLine($"Exception: {exception.Exception}");
                            return Task.CompletedTask;
                        })
                    {
                        AutoComplete = true,
                        MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                    }
                );

                await Task.Delay(TimeSpan.FromSeconds(25));

                await client.SendAsync(messages);
                Console.WriteLine("Messages sent");

                Console.ReadLine();
            }
            finally
            {
                await client.CloseAsync();
            }
        }
    }
}