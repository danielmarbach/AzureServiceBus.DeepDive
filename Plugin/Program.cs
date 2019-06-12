using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Send
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string destination = "queue";

        static TaskCompletionSource<bool> syncEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        class Plugin : ServiceBusPlugin
        {
            public override string Name => "MyCustomPlugin";

            public override Task<Message> BeforeMessageSend(Message message)
            {
                var currentBody = Encoding.UTF8.GetString(message.Body);
                message.Body = Encoding.UTF8.GetBytes($"Yes I can before send{Environment.NewLine}{currentBody}");
                return Task.FromResult(message);
            }

            public override Task<Message> AfterMessageReceive(Message message)
            {
                var currentBody = Encoding.UTF8.GetString(message.Body);
                message.Body = Encoding.UTF8.GetBytes($"{currentBody}{Environment.NewLine}Yes I can before receive");
                return Task.FromResult(message);
            }
        }

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);
            client.RegisterPlugin(new Plugin());

            try
            {
                await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Deep Dive")));
                Console.WriteLine("Message sent");

                client.RegisterMessageHandler(
                    (message, token) =>
                    {
                        Console.WriteLine($"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                        syncEvent.TrySetResult(true);
                        return Task.CompletedTask;
                    },
                    new MessageHandlerOptions(
                        exception =>
                        {
                            return Task.CompletedTask;
                        })
                    {
                        AutoComplete = true,
                        MaxConcurrentCalls = 1,
                        MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                    }
                );

                await syncEvent.Task;

            }
            finally
            {
                await client.CloseAsync();
            }
        }
    }
}
