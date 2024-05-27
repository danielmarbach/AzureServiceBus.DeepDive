using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
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

            var messagesToSend = new Queue<ServiceBusMessage>();
            for (var i = 0; i < 11; i++)
            {
                var message = new ServiceBusMessage(GenerateDataKb(100));
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
        
        static string GenerateDataKb(int nrOfKilobytes)
        {
            // Define the size of the string in bytes
            int sizeInBytes = nrOfKilobytes * 1024; 

            // Create a small string to repeat
            string smallString = "a";

            // Calculate how many repetitions are needed to reach the desired size
            int repeatCount = sizeInBytes / Encoding.UTF8.GetByteCount(smallString);

            // Use StringBuilder for efficient string concatenation
            var sb = new StringBuilder(sizeInBytes);

            for (int i = 0; i < repeatCount; i++)
            {
                sb.Append(smallString);
            }

            // Convert the StringBuilder to a string
            return sb.ToString();
        }
    }
}