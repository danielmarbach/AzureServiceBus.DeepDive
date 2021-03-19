using Azure.Messaging.ServiceBus;
using System;
using System.Threading.Tasks;
using static System.Console;

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

            WriteLine("netstat -na | find \"5671 \"");

            await using var serviceBusClient = new ServiceBusClient(connectionString);
            await using var connectionSharingSender = serviceBusClient.CreateSender(destination);
            await connectionSharingSender.SendMessageAsync(new ServiceBusMessage("Deep Dive"));
            await using var connectionSharingReceiver = serviceBusClient.CreateReceiver(destination);
            await connectionSharingReceiver.ReceiveMessageAsync();

            WriteLine("Continue with a dedicated connection");
            ReadLine();

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

            WriteLine("Enter to stop");
            ReadLine();
        }
    }
}