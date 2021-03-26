using Azure.Messaging.ServiceBus;
using System;
using System.Threading.Tasks;
using static System.Console;

namespace RuntimeInfo
{
    using Azure.Messaging.ServiceBus.Administration;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string inputQueue = "queue";
        private static readonly string topicName = "mytopic";
        private static readonly string subscriptionName = "mysubscription";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, inputQueue, topicName, subscriptionName);

            await using var serviceBusClient = new ServiceBusClient(connectionString);
            await using var queueSender = serviceBusClient.CreateSender(inputQueue);
            await queueSender.SendMessageAsync(new ServiceBusMessage("Kick off"));

            await using var topicSender = serviceBusClient.CreateSender(topicName);
            await topicSender.SendMessageAsync(new ServiceBusMessage("Kick off"));

            var client = new ServiceBusAdministrationClient(connectionString);

            NamespaceProperties namespaceInfo = await client.GetNamespacePropertiesAsync();
            WriteLine($"Namespace Information about '{namespaceInfo.Name}'");
            WriteLine($"{nameof(namespaceInfo.Alias)}: {namespaceInfo.Alias}");
            WriteLine($"{nameof(namespaceInfo.CreatedTime)}: {namespaceInfo.CreatedTime}");
            WriteLine($"{nameof(namespaceInfo.MessagingSku)}: {namespaceInfo.MessagingSku}");
            WriteLine($"{nameof(namespaceInfo.MessagingUnits)}: {namespaceInfo.MessagingUnits}");
            WriteLine($"{nameof(namespaceInfo.ModifiedTime)}: {namespaceInfo.ModifiedTime}");
            WriteLine($"{nameof(namespaceInfo.Name)}: {namespaceInfo.Name}");
            WriteLine($"{nameof(namespaceInfo.MessagingUnits)}: {namespaceInfo.MessagingUnits}");
            WriteLine();

            QueueRuntimeProperties inputQueueInfo = await client.GetQueueRuntimePropertiesAsync(inputQueue);
            WriteLine($"Queue Information about '{inputQueue}'");
            WriteLine($"{nameof(inputQueueInfo.AccessedAt)}: {inputQueueInfo.AccessedAt}");
            WriteLine($"{nameof(inputQueueInfo.CreatedAt)}: {inputQueueInfo.CreatedAt}");
            WriteLine($"{nameof(inputQueueInfo.TotalMessageCount)}: {inputQueueInfo.TotalMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.ActiveMessageCount)}: {inputQueueInfo.ActiveMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.DeadLetterMessageCount)}: {inputQueueInfo.DeadLetterMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.ScheduledMessageCount)}: {inputQueueInfo.ScheduledMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.TransferDeadLetterMessageCount)}: {inputQueueInfo.TransferDeadLetterMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.TransferMessageCount)}: {inputQueueInfo.TransferMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.Name)}: {inputQueueInfo.Name}");
            WriteLine($"{nameof(inputQueueInfo.SizeInBytes)}: {inputQueueInfo.SizeInBytes}");
            WriteLine($"{nameof(inputQueueInfo.UpdatedAt)}: {inputQueueInfo.UpdatedAt}");
            WriteLine();

            TopicRuntimeProperties topicInfo = await client.GetTopicRuntimePropertiesAsync(topicName);
            WriteLine($"TopicInformation Information about '{topicName}'");
            WriteLine($"{nameof(topicInfo.AccessedAt)}: {topicInfo.AccessedAt}");
            WriteLine($"{nameof(topicInfo.CreatedAt)}: {topicInfo.CreatedAt}");
            WriteLine($"{nameof(topicInfo.ScheduledMessageCount)}: {topicInfo.ScheduledMessageCount}");
            WriteLine($"{nameof(topicInfo.SubscriptionCount)}: {topicInfo.SubscriptionCount}");
            WriteLine($"{nameof(topicInfo.Name)}: {topicInfo.Name}");
            WriteLine($"{nameof(topicInfo.SizeInBytes)}: {topicInfo.SizeInBytes}");
            WriteLine($"{nameof(topicInfo.SubscriptionCount)}: {topicInfo.SubscriptionCount}");
            WriteLine($"{nameof(topicInfo.UpdatedAt)}: {topicInfo.UpdatedAt}");
            WriteLine();

            SubscriptionRuntimeProperties subscriptionInfo = await client.GetSubscriptionRuntimePropertiesAsync(topicName, subscriptionName);
            WriteLine($"Subscription Information about '{subscriptionName}'");
            WriteLine($"{nameof(subscriptionInfo.AccessedAt)}: {subscriptionInfo.AccessedAt}");
            WriteLine($"{nameof(subscriptionInfo.CreatedAt)}: {subscriptionInfo.CreatedAt}");
            WriteLine($"{nameof(subscriptionInfo.TotalMessageCount)}: {subscriptionInfo.TotalMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.TotalMessageCount)}: {inputQueueInfo.TotalMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.ActiveMessageCount)}: {inputQueueInfo.ActiveMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.DeadLetterMessageCount)}: {inputQueueInfo.DeadLetterMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.ScheduledMessageCount)}: {inputQueueInfo.ScheduledMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.TransferDeadLetterMessageCount)}: {inputQueueInfo.TransferDeadLetterMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.TransferMessageCount)}: {inputQueueInfo.TransferMessageCount}");
            WriteLine($"{nameof(subscriptionInfo.SubscriptionName)}: {subscriptionInfo.SubscriptionName}");
            WriteLine($"{nameof(subscriptionInfo.TopicName)}: {subscriptionInfo.TopicName}");
            WriteLine($"{nameof(subscriptionInfo.UpdatedAt)}: {subscriptionInfo.UpdatedAt}");
            WriteLine();

            ReadLine();
        }
    }
}