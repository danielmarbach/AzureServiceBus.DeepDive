using System;
using System.Threading.Tasks;

namespace RuntimeInfo
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string inputQueue, string topicName, string subscriptionName)
        {
            var client = await Cleanup(connectionString, topicName, subscriptionName);

            var topicDescription = new CreateTopicOptions(topicName);
            await client.CreateTopicAsync(topicDescription);

            var subscriptionDescription = new CreateSubscriptionOptions(topicName, subscriptionName);
            await client.CreateSubscriptionAsync(subscriptionDescription);


            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new CreateQueueOptions(inputQueue) { MaxDeliveryCount = 2 });
        }

         private static async Task<ServiceBusAdministrationClient> Cleanup(string connectionString, string topicName,
            string subscriptionName)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            if (await client.SubscriptionExistsAsync(topicName, subscriptionName))
                await client.DeleteSubscriptionAsync(topicName, subscriptionName);

            if (await client.TopicExistsAsync(topicName)) await client.DeleteTopicAsync(topicName);

            return client;
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            QueueRuntimeProperties info = await client.GetQueueRuntimePropertiesAsync(destination);

            Console.WriteLine($"#'{info.ActiveMessageCount}' messages in '{destination}'");
        }
    }
}