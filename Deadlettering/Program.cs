using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Deadlettering
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

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

            var message = new ServiceBusMessage("Half life") { TimeToLive = TimeSpan.FromSeconds(1) };
            await sender.SendMessageAsync(message);
            Console.WriteLine("Sent half life message");

            message = new ServiceBusMessage("Delivery Count");
            await sender.SendMessageAsync(message);
            Console.WriteLine("Sent delivery count message");

            message = new ServiceBusMessage("Poor Soul");
            message.ApplicationProperties.Add("Yehaa", "Why so happy?");
            await sender.SendMessageAsync(message);
            Console.WriteLine("Sent poor soul message");

            await Task.Delay(2000);

            await using var receiver = serviceBusClient.CreateProcessor(destination, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages =  false,
                MaxConcurrentCalls = 3
            });

            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;
                switch (Encoding.UTF8.GetString(message.Body))
                {
                    case "Half life":
                        await processMessageEventArgs.AbandonMessageAsync(message);
                        await Console.Error.WriteLineAsync("Abandon half life message");
                        break;
                    case "Delivery Count":
                        await Console.Error.WriteLineAsync("Throwing delivery count message");
                        throw new InvalidOperationException();
                    case "Poor Soul":
                        await Console.Error.WriteLineAsync("Dead letter poor soul message");
                        await processMessageEventArgs.DeadLetterMessageAsync(message,
                            new Dictionary<string, object>
                            {
                                {"Reason", "Because we can!"},
                                {"When", DateTimeOffset.UtcNow}
                            });
                        break;
                }
            };
            receiver.ProcessErrorAsync += _ => Task.CompletedTask;
            await receiver.StartProcessingAsync();

            await Task.Delay(5000); // don't do this at home

            await receiver.StopProcessingAsync();

            await receiver.CloseAsync();
        }
    }
}