using System;
using System.Threading.Tasks;
using static System.Console;
using static System.Text.Encoding;

namespace Receive
{
    using System.Collections.Generic;
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
            int numberOfMessages = int.Parse(args[0]);

            await using var stage = await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(60)
                }
            });

            await using var sender = serviceBusClient.CreateSender(destination);
            var messages = new List<ServiceBusMessage>(numberOfMessages);
            for (int i = 0; i < numberOfMessages; i++)
            {
                var message = new ServiceBusMessage(UTF8.GetBytes($"Deep Dive {i} Deep Dive {i} Deep Dive {i} Deep Dive {i} Deep Dive {i} Deep Dive {i}"));
                messages.Add(message);
                Console.WriteLine(message.Body);

                if (i % 1000 == 0)
                {
                    await sender.SendMessagesAsync(messages);
                    messages.Clear();
                }
            }

            await sender.SendMessagesAsync(messages);

            WriteLine("Message sent");
            Console.WriteLine("Take snapshot");
            Console.ReadLine();

            var countDownEvent = new CountdownEvent(numberOfMessages);

            var processorOptions = new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 100,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10),
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                PoolMessageBodies = true,
            };

            await using var receiver = serviceBusClient.CreateProcessor(destination, processorOptions);
            receiver.ProcessMessageAsync += async messageEventArgs =>
            {
                var message = messageEventArgs.Message;
                await Out.WriteLineAsync(
                    $"Received message with '{message.MessageId}' and content {message.Body}");
                await messageEventArgs.CompleteMessageAsync(message);
                countDownEvent.Signal();
            };
            receiver.ProcessErrorAsync += async errorEventArgs =>
            {
                await Out.WriteLineAsync($"Exception: {errorEventArgs.Exception}");
                await Out.WriteLineAsync($"FullyQualifiedNamespace: {errorEventArgs.FullyQualifiedNamespace}");
                await Out.WriteLineAsync($"ErrorSource: {errorEventArgs.ErrorSource}");
                await Out.WriteLineAsync($"EntityPath: {errorEventArgs.EntityPath}");
            };

            await receiver.StartProcessingAsync();

            countDownEvent.Wait();

            Console.WriteLine("Take snapshot");
            Console.ReadLine();

            await receiver.StopProcessingAsync();
        }
    }
}