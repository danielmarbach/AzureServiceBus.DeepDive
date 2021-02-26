using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace Send
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString);

            await using var client = serviceBusClient.CreateSender(destination);
            var message = new ServiceBusMessage("Deep Dive");

            message.ApplicationProperties.Add("TenantId", "MyTenantId");
            // explore a few more properties

            await client.SendMessageAsync(message);
        }
    }
}