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

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, destinationQueue);

            var client = new QueueClient(connectionString, inputQueue);
            await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Kick off")));
            await client.CloseAsync();

            var connection = new ServiceBusConnection(connectionString);
            var receiver = new MessageReceiver(connection, inputQueue);
            var sender = new MessageSender(connection, destinationQueue);

            await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);

            receiver.RegisterMessageHandler(
                async (message, token) =>
                {
                    Console.WriteLine(
                        $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                    await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will leak")));
                    throw new InvalidOperationException();
                },
                new MessageHandlerOptions(
                    async exception => { await Prepare.ReportNumberOfMessages(connectionString, destinationQueue); })
                {
                    AutoComplete = false,
                    MaxConcurrentCalls = 1,
                    MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                }
            );
            Console.ReadLine();
            await receiver.CloseAsync();
            await sender.CloseAsync();

            await Prepare.Stage(connectionString, inputQueue, destinationQueue);

            client = new QueueClient(connectionString, inputQueue);
            await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Fail")));
            var winMessage = new Message(Encoding.UTF8.GetBytes("Win"));
            winMessage.UserProperties.Add("Win", true);
            await client.SendAsync(winMessage);
            await client.CloseAsync();

            // connection has to be shared
            receiver = new MessageReceiver(connection, inputQueue);
            sender = new MessageSender(connection, destinationQueue, inputQueue);
            receiver.RegisterMessageHandler(
                async (message, token) =>
                {
                    using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                    {
                        Console.WriteLine(
                            $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                        await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));

                        if (!message.UserProperties.ContainsKey("Win")) throw new InvalidOperationException();

                        await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));

                        scope.Complete();
                    }

                    await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                },
                new MessageHandlerOptions(
                    async exception => { await Prepare.ReportNumberOfMessages(connectionString, destinationQueue); })
                {
                    AutoComplete = false,
                    MaxConcurrentCalls = 1,
                    MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                }
            );
            Console.ReadLine();

            await receiver.CloseAsync();
            await sender.CloseAsync();
            await connection.CloseAsync();
        }
    }
}