using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace TransferDLQ
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string inputQueue, string destinationQueue)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new CreateQueueOptions(inputQueue) { MaxDeliveryCount = 1 });

            if (await client.QueueExistsAsync(destinationQueue)) await client.DeleteQueueAsync(destinationQueue);
            await client.CreateQueueAsync(destinationQueue);
        }

        public static async Task Hazard(string connectionString, string destinationQueue)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(destinationQueue))
            {
                QueueProperties queueProperties = await client.GetQueueAsync(destinationQueue);
                queueProperties.Status = EntityStatus.SendDisabled;

                await client.UpdateQueueAsync(queueProperties);
            }
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            QueueRuntimeProperties info = await client.GetQueueRuntimePropertiesAsync(destination);

            long activeMessageCount = info.ActiveMessageCount;
            long deadLetterMessageCount = info.DeadLetterMessageCount;
            long transferDeadLetterMessageCount = info.TransferDeadLetterMessageCount;

            string destinationDeadLetterPath = EntityNameHelper.FormatDeadLetterPath(destination);
            string destinationTransferDeadLetterPath = EntityNameHelper.FormatTransferDeadLetterPath(destination);

            Console.WriteLine($"#'{activeMessageCount}' messages in '{destination}'");
            Console.WriteLine(
                $"#'{deadLetterMessageCount}' messages in '{destinationDeadLetterPath}'");
            Console.WriteLine(
                $"#'{transferDeadLetterMessageCount}' messages in '{destinationTransferDeadLetterPath}'");
        }
    }
}