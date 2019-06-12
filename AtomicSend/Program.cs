using System;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Microsoft.Azure.ServiceBus;

namespace AtomicSend
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var message = new Message(Encoding.UTF8.GetBytes("Deep Dive 1"));
                await client.SendAsync(message);
                Console.WriteLine(
                    $"Sent message 1 in transaction '{Transaction.Current.TransactionInformation.LocalIdentifier}'");

                await Prepare.ReportNumberOfMessages(connectionString, destination);

                message = new Message(Encoding.UTF8.GetBytes("Deep Dive 2"));
                await client.SendAsync(message);
                Console.WriteLine(
                    $"Sent message 2 in transaction '{Transaction.Current.TransactionInformation.LocalIdentifier}'");

                await Prepare.ReportNumberOfMessages(connectionString, destination);

                scope.Complete();
            }

            await Prepare.ReportNumberOfMessages(connectionString, destination);

            await client.CloseAsync();
        }
    }
}