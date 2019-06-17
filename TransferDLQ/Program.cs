using System;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace TransferDLQ
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
            var sender = new MessageSender(connection, destinationQueue, inputQueue);
            receiver.RegisterMessageHandler(
                async (message, token) =>
                {
                    using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                    {
                        Console.WriteLine(
                            $"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                        await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));

                        await Prepare.Hazard(connectionString, destinationQueue);

                        scope.Complete();
                    }

                    await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
                }, Prepare.Options(connectionString, inputQueue)
            );
            Console.ReadLine();

            await Prepare.ReportNumberOfMessages(connectionString, inputQueue);

            await receiver.CloseAsync();
            await sender.CloseAsync();
            await connection.CloseAsync();
        }
    }
}