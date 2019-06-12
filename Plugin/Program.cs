using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Plugin
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static readonly TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);
            client.RegisterPlugin(new MyCustomPlugin());

            await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Deep Dive")));
            Console.WriteLine("Message sent");

            client.RegisterMessageHandler(
                (message, token) =>
                {
                    Console.WriteLine(
                        $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                    syncEvent.TrySetResult(true);
                    return Task.CompletedTask;
                },
                Prepare.Options
            );

            await syncEvent.Task;
            await client.CloseAsync();
        }
    }
}