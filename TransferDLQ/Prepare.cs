using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace TransferDLQ
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string inputQueue, string destinationQueue)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(inputQueue))
            {
                await client.DeleteQueueAsync(inputQueue);
            }
            await client.CreateQueueAsync(new QueueDescription(inputQueue) { MaxDeliveryCount = 1 });

            if (await client.QueueExistsAsync(destinationQueue))
            {
                await client.DeleteQueueAsync(destinationQueue);
            }
            await client.CreateQueueAsync(destinationQueue);

            await client.CloseAsync();
        }

        public static async Task Hazard(string connectionString, string destinationQueue)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(destinationQueue))
            {
                await client.DeleteQueueAsync(destinationQueue);
            }

            await client.CloseAsync();
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);

            var info = await client.GetQueueRuntimeInfoAsync(destination);

            await client.CloseAsync();

            Console.WriteLine($"#'{info.MessageCount}' messages in '{destination}'");
            Console.WriteLine($"#'{info.MessageCountDetails.DeadLetterMessageCount}' messages in '{EntityNameHelper.FormatDeadLetterPath(destination)}'");
            Console.WriteLine($"#'{info.MessageCountDetails.TransferDeadLetterMessageCount}' messages in '{EntityNameHelper.FormatTransferDeadLetterPath(destination)}'");
        }
    }
}