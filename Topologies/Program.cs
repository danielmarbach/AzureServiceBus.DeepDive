using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Azure.ServiceBus.Management;

namespace Topologies
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string topicName = "topic";
        static string rushSubscription = "alwaysInRush";
        static string currencySubscription = "maybeRich";

        static string inputQueue = "queue";

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, topicName, rushSubscription, currencySubscription);

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

            var receiver = new MessageReceiver(connectionString, inputQueue);
            try
            {
                var receivedMessages = await receiver.ReceiveAsync(2);
                foreach(var receivedMessage in receivedMessages) 
                {
                    var body = Encoding.UTF8.GetString(receivedMessage.Body);
                    var label = receivedMessage.Label;
                    receivedMessage.UserProperties.TryGetValue("currency", out var currency);
                    Console.WriteLine($"{body} / Label = '{label}' / Currency = '{currency}'");
                }
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
            finally
            {
                await receiver.CloseAsync();
            }
        }
    }
}
