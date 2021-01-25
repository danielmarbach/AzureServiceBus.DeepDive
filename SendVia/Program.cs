using System;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace SendVia
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string inputQueue = "queue";
        private static readonly string destinationQueue = "destination";
        private static readonly string anotherDestinationQueue = "anotherdestination";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, destinationQueue, anotherDestinationQueue);

            var client = new QueueClient(connectionString, inputQueue);
            await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Kick off")));
            await client.CloseAsync();

            var connection = new ServiceBusConnection(connectionString);
            var receiver = new MessageReceiver(connection, inputQueue);
            var sender = new MessageSender(connection, destinationQueue);

            await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
            await Prepare.ReportNumberOfMessages(connectionString, anotherDestinationQueue);

            receiver.RegisterMessageHandler(
                async (message, token) =>
                {
                    Console.WriteLine(
                        $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                    await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will leak")));
                    throw new InvalidOperationException();
                },
                Prepare.Options(connectionString, destinationQueue, anotherDestinationQueue)
            );
            Console.ReadLine();
            await receiver.CloseAsync();
            await sender.CloseAsync();

            await Prepare.Stage(connectionString, inputQueue, destinationQueue, anotherDestinationQueue);

            client = new QueueClient(connectionString, inputQueue);
            await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Fail")));
            var winMessage = new Message(Encoding.UTF8.GetBytes("Win"));
            winMessage.UserProperties.Add("Win", true);
            await client.SendAsync(winMessage);
            await client.CloseAsync();

            // connection has to be shared
            receiver = new MessageReceiver(connection, inputQueue);
            sender = new MessageSender(connection, destinationQueue, inputQueue);
            var anotherSender = new MessageSender(connection, anotherDestinationQueue, inputQueue);
            receiver.RegisterMessageHandler(
                async (message, token) =>
                {
                    using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                    {
                        Console.WriteLine(
                            $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");

                        await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));
                        await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));

                        await anotherSender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));
                        await anotherSender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));

                        if (!message.UserProperties.ContainsKey("Win")) throw new InvalidOperationException();

                        scope.Complete();
                    }

                    await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                    await Prepare.ReportNumberOfMessages(connectionString, anotherDestinationQueue);
                },
                Prepare.Options(connectionString, destinationQueue, anotherDestinationQueue)
            );
            Console.ReadLine();

            await receiver.CloseAsync();
            await sender.CloseAsync();
            await anotherSender.CloseAsync();
            await connection.CloseAsync();
        }
    }
}