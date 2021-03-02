using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Pubsub
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string topicName = "topic";
        private static readonly string rushSubscription = "alwaysInRush";
        private static readonly string currencySubscription = "maybeRich";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, topicName, rushSubscription, currencySubscription);

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

            Console.WriteLine("Sent message");
        }
    }
}