using Azure.Messaging.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;
using System;
using System.Threading.Tasks;
using static System.Console;

namespace SendVia
{
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

            var client = new ManagementClient(connectionString);

            var namespaceInfo = await client.GetNamespaceInfoAsync();
            WriteLine($"Namespace Information about '{namespaceInfo.Name}'");
            WriteLine($"{nameof(namespaceInfo.Alias)}: {namespaceInfo.Alias}");
            WriteLine($"{nameof(namespaceInfo.CreatedTime)}: {namespaceInfo.CreatedTime}");
            WriteLine($"{nameof(namespaceInfo.MessagingSku)}: {namespaceInfo.MessagingSku}");
            WriteLine($"{nameof(namespaceInfo.MessagingUnits)}: {namespaceInfo.MessagingUnits}");
            WriteLine($"{nameof(namespaceInfo.ModifiedTime)}: {namespaceInfo.ModifiedTime}");
            WriteLine($"{nameof(namespaceInfo.Name)}: {namespaceInfo.Name}");
            WriteLine($"{nameof(namespaceInfo.NamespaceType)}: {namespaceInfo.NamespaceType}");
            WriteLine();

            var inputQueueInfo = await client.GetQueueRuntimeInfoAsync(inputQueue);
            WriteLine($"Queue Information about '{inputQueue}'");
            WriteLine($"{nameof(inputQueueInfo.AccessedAt)}: {inputQueueInfo.AccessedAt}");
            WriteLine($"{nameof(inputQueueInfo.CreatedAt)}: {inputQueueInfo.CreatedAt}");
            WriteLine($"{nameof(inputQueueInfo.MessageCount)}: {inputQueueInfo.MessageCount}");
            WriteLine($"{nameof(inputQueueInfo.MessageCountDetails)}.{nameof(inputQueueInfo.MessageCountDetails.ActiveMessageCount)}: {inputQueueInfo.MessageCountDetails.ActiveMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.MessageCountDetails)}.{nameof(inputQueueInfo.MessageCountDetails.DeadLetterMessageCount)}: {inputQueueInfo.MessageCountDetails.DeadLetterMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.MessageCountDetails)}.{nameof(inputQueueInfo.MessageCountDetails.ScheduledMessageCount)}: {inputQueueInfo.MessageCountDetails.ScheduledMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.MessageCountDetails)}.{nameof(inputQueueInfo.MessageCountDetails.TransferDeadLetterMessageCount)}: {inputQueueInfo.MessageCountDetails.TransferDeadLetterMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.MessageCountDetails)}.{nameof(inputQueueInfo.MessageCountDetails.TransferMessageCount)}: {inputQueueInfo.MessageCountDetails.TransferMessageCount}");
            WriteLine($"{nameof(inputQueueInfo.Path)}: {inputQueueInfo.Path}");
            WriteLine($"{nameof(inputQueueInfo.SizeInBytes)}: {inputQueueInfo.SizeInBytes}");
            WriteLine($"{nameof(inputQueueInfo.UpdatedAt)}: {inputQueueInfo.UpdatedAt}");
            WriteLine();

            var topicInfo = await client.GetTopicRuntimeInfoAsync(topicName);
            WriteLine($"TopicInformation Information about '{topicName}'");
            WriteLine($"{nameof(topicInfo.AccessedAt)}: {topicInfo.AccessedAt}");
            WriteLine($"{nameof(topicInfo.CreatedAt)}: {topicInfo.CreatedAt}");
            WriteLine($"{nameof(topicInfo.MessageCountDetails)}.{nameof(topicInfo.MessageCountDetails.ActiveMessageCount)}: {topicInfo.MessageCountDetails.ActiveMessageCount}");
            WriteLine($"{nameof(topicInfo.MessageCountDetails)}.{nameof(topicInfo.MessageCountDetails.DeadLetterMessageCount)}: {topicInfo.MessageCountDetails.DeadLetterMessageCount}");
            WriteLine($"{nameof(topicInfo.MessageCountDetails)}.{nameof(topicInfo.MessageCountDetails.ScheduledMessageCount)}: {topicInfo.MessageCountDetails.ScheduledMessageCount}");
            WriteLine($"{nameof(topicInfo.MessageCountDetails)}.{nameof(topicInfo.MessageCountDetails.TransferDeadLetterMessageCount)}: {topicInfo.MessageCountDetails.TransferDeadLetterMessageCount}");
            WriteLine($"{nameof(topicInfo.MessageCountDetails)}.{nameof(topicInfo.MessageCountDetails.TransferMessageCount)}: {topicInfo.MessageCountDetails.TransferMessageCount}");
            WriteLine($"{nameof(topicInfo.Path)}: {topicInfo.Path}");
            WriteLine($"{nameof(topicInfo.SizeInBytes)}: {topicInfo.SizeInBytes}");
            WriteLine($"{nameof(topicInfo.SubscriptionCount)}: {topicInfo.SubscriptionCount}");
            WriteLine($"{nameof(topicInfo.UpdatedAt)}: {topicInfo.UpdatedAt}");
            WriteLine();

            var subscriptionInfo = await client.GetSubscriptionRuntimeInfoAsync(topicName, subscriptionName);
            WriteLine($"Subscription Information about '{subscriptionName}'");
            WriteLine($"{nameof(subscriptionInfo.AccessedAt)}: {subscriptionInfo.AccessedAt}");
            WriteLine($"{nameof(subscriptionInfo.CreatedAt)}: {subscriptionInfo.CreatedAt}");
            WriteLine($"{nameof(subscriptionInfo.MessageCount)}: {subscriptionInfo.MessageCount}");
            WriteLine($"{nameof(subscriptionInfo.MessageCountDetails)}.{nameof(subscriptionInfo.MessageCountDetails.ActiveMessageCount)}: {subscriptionInfo.MessageCountDetails.ActiveMessageCount}");
            WriteLine($"{nameof(subscriptionInfo.MessageCountDetails)}.{nameof(subscriptionInfo.MessageCountDetails.DeadLetterMessageCount)}: {subscriptionInfo.MessageCountDetails.DeadLetterMessageCount}");
            WriteLine($"{nameof(subscriptionInfo.MessageCountDetails)}.{nameof(subscriptionInfo.MessageCountDetails.ScheduledMessageCount)}: {subscriptionInfo.MessageCountDetails.ScheduledMessageCount}");
            WriteLine($"{nameof(subscriptionInfo.MessageCountDetails)}.{nameof(subscriptionInfo.MessageCountDetails.TransferDeadLetterMessageCount)}: {subscriptionInfo.MessageCountDetails.TransferDeadLetterMessageCount}");
            WriteLine($"{nameof(subscriptionInfo.MessageCountDetails)}.{nameof(subscriptionInfo.MessageCountDetails.TransferMessageCount)}: {subscriptionInfo.MessageCountDetails.TransferMessageCount}");
            WriteLine($"{nameof(subscriptionInfo.SubscriptionName)}: {subscriptionInfo.SubscriptionName}");
            WriteLine($"{nameof(subscriptionInfo.TopicPath)}: {subscriptionInfo.TopicPath}");
            WriteLine($"{nameof(subscriptionInfo.UpdatedAt)}: {subscriptionInfo.UpdatedAt}");
            WriteLine();

            await client.CloseAsync();

            ReadLine();
        }
    }
}