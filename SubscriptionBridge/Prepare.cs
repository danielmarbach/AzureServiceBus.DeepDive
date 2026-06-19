using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;

namespace SubscriptionBridge;

public static class Prepare
{
    const string SalesTopic = "orders";
    const string InventoryTopic = "inventory";
    const string SalesSubscription = "sales-sub";
    const string InventorySubscription = "inventory-sub";
    const string InputQueue = "sales-input";

    public static async Task<IAsyncDisposable> Stage(string connectionString)
    {
        var client = new ServiceBusAdministrationClient(connectionString);

        // Cleanup existing entities
        var cleanupTasks = new[]
        {
            TryDeleteQueue(client, InputQueue),
            TryDeleteSubscription(client, SalesTopic, SalesSubscription),
            TryDeleteSubscription(client, InventoryTopic, InventorySubscription),
            TryDeleteTopic(client, SalesTopic),
            TryDeleteTopic(client, InventoryTopic)
        };
        await Task.WhenAll(cleanupTasks);

        // Create topics
        foreach (var topic in new[] { SalesTopic, InventoryTopic })
        {
            var topicOptions = new CreateTopicOptions(topic);
            await client.CreateTopicAsync(topicOptions);
        }

        // Create session-enabled subscriptions WITHOUT ForwardTo
        foreach (var (topic, sub) in new[] {
            (SalesTopic, SalesSubscription),
            (InventoryTopic, InventorySubscription)
        })
        {
            var subOptions = new CreateSubscriptionOptions(topic, sub)
            {
                RequiresSession = true,
                DefaultMessageTimeToLive = TimeSpan.FromMinutes(10)
            };
            await client.CreateSubscriptionAsync(subOptions);

            await client.DeleteRuleAsync(topic, sub, "$Default");

            var ruleOptions = new CreateRuleOptions
            {
                Name = "AllWithSessionId",
                Filter = new SqlRuleFilter("1=1"),
                Action = null
            };
            await client.CreateRuleAsync(topic, sub, ruleOptions);
        }

        // Create session-enabled input queue
        var queueOptions = new CreateQueueOptions(InputQueue)
        {
            RequiresSession = true,
            DefaultMessageTimeToLive = TimeSpan.FromMinutes(10),
            DeadLetteringOnMessageExpiration = true,
            MaxDeliveryCount = 10
        };
        await client.CreateQueueAsync(queueOptions);

        Console.WriteLine("=== Infrastructure created ===");
        Console.WriteLine($"  Topic: {SalesTopic} -> Subscription: {SalesSubscription} (session-enabled, no ForwardTo)");
        Console.WriteLine($"  Topic: {InventoryTopic} -> Subscription: {InventorySubscription} (session-enabled, no ForwardTo)");
        Console.WriteLine($"  Input Queue: {InputQueue} (session-enabled, shared)");
        Console.WriteLine();

        return new Cleanup(connectionString);
    }

    public static string InputQueueName => InputQueue;
    public static string SalesTopicName => SalesTopic;
    public static string InventoryTopicName => InventoryTopic;
    public static string SalesSub => SalesSubscription;
    public static string InventorySub => InventorySubscription;

    static async Task TryDeleteQueue(ServiceBusAdministrationClient client, string name)
    {
        try
        {
            if (await client.QueueExistsAsync(name))
                await client.DeleteQueueAsync(name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not delete queue '{name}': {ex.Message}");
        }
    }

    static async Task TryDeleteTopic(ServiceBusAdministrationClient client, string name)
    {
        try
        {
            if (await client.TopicExistsAsync(name))
                await client.DeleteTopicAsync(name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not delete topic '{name}': {ex.Message}");
        }
    }

    static async Task TryDeleteSubscription(ServiceBusAdministrationClient client, string topic, string sub)
    {
        try
        {
            if (await client.SubscriptionExistsAsync(topic, sub))
                await client.DeleteSubscriptionAsync(topic, sub);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not delete subscription '{topic}/{sub}': {ex.Message}");
        }
    }

    sealed class Cleanup(string connectionString) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Console.WriteLine();
            Console.WriteLine("=== Cleaning up infrastructure ===");
            var client = new ServiceBusAdministrationClient(connectionString);
            await TryDeleteQueue(client, InputQueue);
            await TryDeleteSubscription(client, SalesTopic, SalesSubscription);
            await TryDeleteSubscription(client, InventoryTopic, InventorySubscription);
            await TryDeleteTopic(client, SalesTopic);
            await TryDeleteTopic(client, InventoryTopic);
            Console.WriteLine("=== Cleanup complete ===");
        }
    }
}