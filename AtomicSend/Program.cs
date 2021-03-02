using System;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Azure.ServiceBus;

namespace AtomicSend
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

            await using var serviceBusClient = new ServiceBusClient(connectionString);

            await using var sender = serviceBusClient.CreateSender(destination);

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var message = new ServiceBusMessage("Deep Dive 1");
                await sender.SendMessageAsync(message);
                Console.WriteLine(
                    $"Sent message 1 in transaction '{Transaction.Current.TransactionInformation.LocalIdentifier}'");

                await Prepare.ReportNumberOfMessages(connectionString, destination);

                message = new ServiceBusMessage("Deep Dive 2");
                await sender.SendMessageAsync(message);
                Console.WriteLine(
                    $"Sent message 2 in transaction '{Transaction.Current.TransactionInformation.LocalIdentifier}'");

                Console.WriteLine("About to complete transaction scope.");
                await Prepare.ReportNumberOfMessages(connectionString, destination);

                scope.Complete();
                Console.WriteLine("Completed transaction scope.");
            }

            await Prepare.ReportNumberOfMessages(connectionString, destination);
        }
    }
}