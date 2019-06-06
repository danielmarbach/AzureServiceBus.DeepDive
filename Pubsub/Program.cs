using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Azure.ServiceBus.Management;

namespace Pubsub
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string topicName = "topic";
        static string rushSubscription = "alwaysInRush";
        static string currencySubscription = "maybeRich";

        static async Task Main(string[] args)
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
