using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Transactions;
using static System.Console;

namespace Batching
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await using var stage = await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString);

            await using var sender = serviceBusClient.CreateSender(destination);

            var messages = new List<ServiceBusMessage>();
            for (var i = 0; i < 10; i++)
            {
                var message = new ServiceBusMessage("Deep Dive{i}");
                messages.Add(message);
            }

            WriteLine($"Sending {messages.Count} messages in a batch.");
            await sender.SendMessagesAsync(messages);
            messages.Clear();
            WriteLine();

            for (var i = 0; i < 6500; i++)
            {
                var message = new ServiceBusMessage($"Deep Dive{i}");
                messages.Add(message);
            }

            try
            {
                WriteLine($"Sending {messages.Count} messages in a batch.");
                await sender.SendMessagesAsync(messages);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageSizeExceeded)
            {
                await Error.WriteLineAsync(ex.Message);
            }

            messages.Clear();
            WriteLine();

            for (var i = 0; i < 101; i++)
            {
                var message = new ServiceBusMessage($"Deep Dive{i}");
                messages.Add(message);
            }

            try
            {
                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    WriteLine($"Sending {messages.Count} messages in a batch with in transaction '{Transaction.Current.TransactionInformation.LocalIdentifier}'.");
                    await sender.SendMessagesAsync(messages);
                    scope.Complete();
                }
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.QuotaExceeded)
            {
                await Error.WriteLineAsync(ex.Message);
            }

            var messagesToSend = new Queue<ServiceBusMessage>();
            for (var i = 0; i < 4500; i++)
            {
                var message = new ServiceBusMessage($"Deep Dive{i}. Deep Dive{i}. Deep Dive{i}.");
                messagesToSend.Enqueue(message);
            }

            var messageCount = messagesToSend.Count;
            int batchCount = 1;
            while (messagesToSend.Count > 0)
            {
                using ServiceBusMessageBatch messageBatch = await sender.CreateMessageBatchAsync();

                if (messageBatch.TryAddMessage(messagesToSend.Peek()))
                {
                    messagesToSend.Dequeue();
                }
                else
                {
                    throw new Exception($"Message {messageCount - messagesToSend.Count} is too large and cannot be sent.");
                }

                while (messagesToSend.Count > 0 && messageBatch.TryAddMessage(messagesToSend.Peek()))
                {
                    messagesToSend.Dequeue();
                }

                WriteLine($"Sending {messageBatch.Count} messages in a batch {batchCount++}.");
                await sender.SendMessagesAsync(messageBatch);
            }
        }
    }
}