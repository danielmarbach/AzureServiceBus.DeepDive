using System;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace TransferDLQ
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string inputQueue = "queue";
        static string destinationQueue = "destination";

        static TaskCompletionSource<bool> syncEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, destinationQueue);

            var client = new QueueClient(connectionString, inputQueue);
            await client.SendAsync(new Message(Encoding.UTF8.GetBytes("Kick off")));
            await client.CloseAsync();

            var connection = new ServiceBusConnection(connectionString);
            // connection has to be shared
            var receiver = new MessageReceiver(connection, inputQueue);
            var sender = new MessageSender(connection, entityPath: destinationQueue, viaEntityPath: inputQueue);
            receiver.RegisterMessageHandler(
                    async (message, token) =>
                    {
                        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                        {
                            Console.WriteLine($"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                            await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Will not leak")));

                            await Prepare.Hazard(connectionString, destinationQueue);

                            scope.Complete();
                        }

                        await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
                    },
                    new MessageHandlerOptions(
                        async exception =>
                        {
                            await Prepare.ReportNumberOfMessages(connectionString, inputQueue);
                        })
                    {
                        AutoComplete = true,
                        MaxConcurrentCalls = 1,
                        MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
                    }
                );
            Console.ReadLine();

            await Prepare.ReportNumberOfMessages(connectionString, inputQueue);

            await receiver.CloseAsync();
            await sender.CloseAsync();
            await connection.CloseAsync();
        }
    }
}
