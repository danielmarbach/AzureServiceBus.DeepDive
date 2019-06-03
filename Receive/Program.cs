﻿using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Send
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
                await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Deep Dive")));
                Console.WriteLine("Message sent");

                client.RegisterMessageHandler(
                    async (message, token) =>
                    {
                        Console.WriteLine($"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                        // throw new InvalidOperationException();
                        await client.CompleteAsync(message.SystemProperties.LockToken);
                        syncEvent.TrySetResult(true);
                    },
                    new MessageHandlerOptions(
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
                        AutoComplete = false,
                        MaxConcurrentCalls = 1,
                        MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                    }
                );

                await syncEvent.Task;

            }
            finally
            {
                await client.CloseAsync();
                await Prepare.LeaveStage(connectionString, destination);
            }
        }
    }
}
