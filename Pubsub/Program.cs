using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Pubsub
{
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

            var client = new MessageSender(connectionString, topicName);
            var message = new Message();
            message.Body = Encoding.UTF8.GetBytes("Damn I have not time!");
            message.Label = "rush";
            await client.SendAsync(message);

            message = new Message();
            message.Body = Encoding.UTF8.GetBytes("I'm rich! I have 1000");
            message.UserProperties.Add("currency", "CHF");
            await client.SendAsync(message);

            await client.CloseAsync();

            Console.WriteLine("Sent message");
        }
    }
}