using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace SendVia
{
    public static class Prepare
    {
        public static MessageHandlerOptions Options(string connectionString, string destinationQueue) => new MessageHandlerOptions(
                    async exception => { await Prepare.ReportNumberOfMessages(connectionString, destinationQueue); })
        {
            AutoComplete = false,
            MaxConcurrentCalls = 1,
            MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
        };

        public static async Task Stage(string connectionString, string inputQueue, string topicName, string subscriptionName)
        {
            var client = await Cleanup(connectionString, topicName, subscriptionName);

            var topicDescription = new TopicDescription(topicName);
            await client.CreateTopicAsync(topicDescription);

            var subscriptionDescription = new SubscriptionDescription(topicName, subscriptionName);
            await client.CreateSubscriptionAsync(subscriptionDescription);
            

            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new QueueDescription(inputQueue) { MaxDeliveryCount = 2 });

            await client.CloseAsync();
        }

         private static async Task<ManagementClient> Cleanup(string connectionString, string topicName,
            string subscriptionName)
        {
            var client = new ManagementClient(connectionString);

            if (await client.SubscriptionExistsAsync(topicName, subscriptionName))
                await client.DeleteSubscriptionAsync(topicName, subscriptionName);

            if (await client.TopicExistsAsync(topicName)) await client.DeleteTopicAsync(topicName);

            return client;
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);

            var info = await client.GetQueueRuntimeInfoAsync(destination);

            await client.CloseAsync();

            Console.WriteLine($"#'{info.MessageCount}' messages in '{destination}'");
        }
    }
}