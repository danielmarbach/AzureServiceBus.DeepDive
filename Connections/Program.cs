using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace Connections
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await using var stage = await Prepare.Stage(connectionString, destination);

            Console.WriteLine("netstat -na | find \"5671\"");

            await using var serviceBusClient = new ServiceBusClient(connectionString);
            await using var connectionSharingSender = serviceBusClient.CreateSender(destination);
            await connectionSharingSender.SendMessageAsync(new ServiceBusMessage("Deep Dive"));
            await using var connectionSharingReceiver = serviceBusClient.CreateReceiver(destination);
            await connectionSharingReceiver.ReceiveMessageAsync();

            Console.WriteLine("Continue with a dedicated connection");
            Console.ReadLine();

            await connectionSharingSender.CloseAsync();
            await connectionSharingSender.DisposeAsync();
            await connectionSharingReceiver.CloseAsync();
            await connectionSharingReceiver.DisposeAsync();
            await serviceBusClient.DisposeAsync();

            GC.Collect();

            await using var senderServiceBusClient = new ServiceBusClient(connectionString);
            await using var receiverServiceBusClient = new ServiceBusClient(connectionString);

            await using var senderWithDedicatedConnection = senderServiceBusClient.CreateSender(destination);
            await using var receiverWithDedicatedConnection = receiverServiceBusClient.CreateReceiver(destination);

            await senderWithDedicatedConnection.SendMessageAsync(new ServiceBusMessage("Deep Dive"));
            await receiverWithDedicatedConnection.ReceiveMessageAsync();

            Console.WriteLine("Enter to stop");
            Console.ReadLine();
        }
    }
}