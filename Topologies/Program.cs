using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Topologies
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string topicName = "topic";
        private static readonly string rushSubscription = "alwaysInRush";
        private static readonly string currencySubscription = "maybeRich";

        private static readonly string inputQueue = "queue";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, topicName, rushSubscription, currencySubscription);

            await using var serviceBusClient = new ServiceBusClient(connectionString);
            await using var sender = serviceBusClient.CreateSender(topicName);

            var message = new ServiceBusMessage("Damn I have no time!")
            {
                Subject = "rush"
            };
            await sender.SendMessageAsync(message);

            message = new ServiceBusMessage("I'm rich! I have 1000");
            message.ApplicationProperties.Add("currency", "CHF");
            await sender.SendMessageAsync(message);

            await using var receiver = serviceBusClient.CreateReceiver(inputQueue);
            var receivedMessages = await receiver.ReceiveMessagesAsync(2);
            foreach (var receivedMessage in receivedMessages)
            {
                var body = Encoding.UTF8.GetString(receivedMessage.Body);
                var label = receivedMessage.Subject;
                receivedMessage.ApplicationProperties.TryGetValue("currency", out var currency);
                Console.WriteLine($"{body} / Label = '{label}' / Currency = '{currency}'");
            }
        }
    }
}