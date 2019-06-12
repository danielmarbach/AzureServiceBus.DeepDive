using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Session
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string destination = "queue";

        static TaskCompletionSource<bool> syncEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);
            try
            {
                var messages = new List<Message> {
                    new Message(Encoding.UTF8.GetBytes("Orange 1")) { SessionId = "Orange" },
                    new Message(Encoding.UTF8.GetBytes("Green 1")) { SessionId = "Green" },
                    new Message(Encoding.UTF8.GetBytes("Blue 1")) { SessionId = "Blue" },
                    new Message(Encoding.UTF8.GetBytes("Green 2")) { SessionId = "Green" },
                    new Message(Encoding.UTF8.GetBytes("Orange 2")) { SessionId = "Orange" },
                    new Message(Encoding.UTF8.GetBytes("Blue 2")) { SessionId = "Blue" },
                    new Message(Encoding.UTF8.GetBytes("Green 3")) { SessionId = "Green" },
                    new Message(Encoding.UTF8.GetBytes("Orange 3")) { SessionId = "Orange" },
                    new Message(Encoding.UTF8.GetBytes("Green 4")) { SessionId = "Green" },
                    new Message(Encoding.UTF8.GetBytes("Purple 1")) { SessionId = "Purple" },
                    new Message(Encoding.UTF8.GetBytes("Blue 3")) { SessionId = "Blue" },
                    new Message(Encoding.UTF8.GetBytes("Orange 4")) { SessionId = "Orange" },
                };

                await client.SendAsync(messages);
                Console.WriteLine("Messages sent");

                client.RegisterSessionHandler(
                    (session, message, token) =>
                    {
                        Console.WriteLine($"Received message on session '{session.SessionId}' with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                        return Task.CompletedTask;
                    },
                    new SessionHandlerOptions(
                        exception =>
                        {
                            Console.WriteLine($"Exception: {exception.Exception}");
                            Console.WriteLine($"Action: {exception.ExceptionReceivedContext.Action}");
                            Console.WriteLine($"ClientId: {exception.ExceptionReceivedContext.ClientId}");
                            Console.WriteLine($"Endpoint: {exception.ExceptionReceivedContext.Endpoint}");
                            Console.WriteLine($"EntityPath: {exception.ExceptionReceivedContext.EntityPath}");
                            return Task.CompletedTask;
                        })
                    {
                        AutoComplete = true,
                        MaxConcurrentSessions = 1,
                        MessageWaitTimeout = TimeSpan.FromSeconds(2),
                        MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                    }
                );

                Console.ReadLine();

            }
            finally
            {
                await client.CloseAsync();
            }
        }
    }
}
