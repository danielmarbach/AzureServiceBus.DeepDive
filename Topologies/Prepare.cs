using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace Topologies
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string inputQueue, string topicName,
            string rushSubscription, string currencySubscription)
        {
            var client = await Cleanup(connectionString, inputQueue, topicName, rushSubscription, currencySubscription);

            var subscriptionDescription = new SubscriptionDescription(topicName, rushSubscription)
            {
                ForwardTo = inputQueue
            };
            await client.CreateSubscriptionAsync(subscriptionDescription);
            subscriptionDescription = new SubscriptionDescription(topicName, currencySubscription)
            {
                ForwardTo = inputQueue
            };
            await client.CreateSubscriptionAsync(subscriptionDescription);

            await client.DeleteRuleAsync(topicName, rushSubscription, RuleDescription.DefaultRuleName);
            await client.DeleteRuleAsync(topicName, currencySubscription, RuleDescription.DefaultRuleName);

            var ruleDescription = new RuleDescription
            {
                Name = "MessagesWithRushlabel",
                Filter = new CorrelationFilter
                {
                    Label = "rush"
                },
                Action = null
            };
            await client.CreateRuleAsync(topicName, rushSubscription, ruleDescription);

            ruleDescription = new RuleDescription
            {
                Name = "MessagesWithCurrencyCHF",
                Filter = new SqlFilter("currency = 'CHF'"),
                Action = new SqlRuleAction("SET currency = 'Złoty'")
            };
            await client.CreateRuleAsync(topicName, currencySubscription, ruleDescription);

            await client.CloseAsync();
        }

        private static async Task<ManagementClient> Cleanup(string connectionString, string inputQueue,
            string topicName, string rushSubscription, string currencySubscription)
        {
            var client = new ManagementClient(connectionString);

            if (await client.SubscriptionExistsAsync(topicName, rushSubscription))
                await client.DeleteSubscriptionAsync(topicName, rushSubscription);

            if (await client.SubscriptionExistsAsync(topicName, currencySubscription))
                await client.DeleteSubscriptionAsync(topicName, currencySubscription);

            if (await client.TopicExistsAsync(topicName)) await client.DeleteTopicAsync(topicName);

            var topicDescription = new TopicDescription(topicName);
            await client.CreateTopicAsync(topicDescription);

            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);

            var queueDescription = new QueueDescription(inputQueue);
            await client.CreateQueueAsync(queueDescription);
            return client;
        }
    }
}