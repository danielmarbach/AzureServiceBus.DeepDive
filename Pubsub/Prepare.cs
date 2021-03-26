using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;

namespace Pubsub
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string topicName, string rushSubscription,
            string currencySubscription)
        {
            var client = await Cleanup(connectionString, topicName, rushSubscription, currencySubscription);

            var subscriptionDescription = new CreateSubscriptionOptions(topicName, rushSubscription);
            await client.CreateSubscriptionAsync(subscriptionDescription);

            subscriptionDescription = new CreateSubscriptionOptions(topicName, currencySubscription);
            await client.CreateSubscriptionAsync(subscriptionDescription);

            await client.DeleteRuleAsync(topicName, rushSubscription, "$Default");
            await client.DeleteRuleAsync(topicName, currencySubscription, "$Default");

            var ruleDescription = new CreateRuleOptions
            {
                Name = "MessagesWithRushlabel",
                Filter = new CorrelationRuleFilter
                {
                    Subject = "rush"
                },
                Action = null
            };
            await client.CreateRuleAsync(topicName, rushSubscription, ruleDescription);

            ruleDescription = new CreateRuleOptions
            {
                Name = "MessagesWithCurrencyCHF",
                Filter = new SqlRuleFilter("currency = 'CHF'"),
                Action = new SqlRuleAction("SET currency = 'Złoty'")
            };
            await client.CreateRuleAsync(topicName, currencySubscription, ruleDescription);
        }

        private static async Task<ServiceBusAdministrationClient> Cleanup(string connectionString, string topicName,
            string rushSubscription, string currencySubscription)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            if (await client.SubscriptionExistsAsync(topicName, rushSubscription))
                await client.DeleteSubscriptionAsync(topicName, rushSubscription);

            if (await client.SubscriptionExistsAsync(topicName, currencySubscription))
                await client.DeleteSubscriptionAsync(topicName, currencySubscription);

            if (await client.TopicExistsAsync(topicName)) await client.DeleteTopicAsync(topicName);

            var topicDescription = new CreateTopicOptions(topicName);
            await client.CreateTopicAsync(topicDescription);

            return client;
        }
    }
}