using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace TransferDLQ
{
    public static class Prepare
    {
        public static MessageHandlerOptions Options(string connectionString, string inputQueue) =>
        new MessageHandlerOptions(
                    async exception => { await Prepare.ReportNumberOfMessages(connectionString, inputQueue); })
        {
            AutoComplete = true,
            MaxConcurrentCalls = 1,
            MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
        };

        public static async Task Stage(string connectionString, string inputQueue, string destinationQueue)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new QueueDescription(inputQueue) { MaxDeliveryCount = 1 });

            if (await client.QueueExistsAsync(destinationQueue)) await client.DeleteQueueAsync(destinationQueue);
            await client.CreateQueueAsync(destinationQueue);

            await client.CloseAsync();
        }

        public static async Task Hazard(string connectionString, string destinationQueue)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(destinationQueue))
            {
                var description = new QueueDescription(destinationQueue)
                {
                    Status = EntityStatus.SendDisabled
                };
                await client.UpdateQueueAsync(description);
            }

            await client.CloseAsync();
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);

            var info = await client.GetQueueRuntimeInfoAsync(destination);
            
            long activeMessageCount = info.MessageCountDetails.ActiveMessageCount;
            long deadLetterMessageCount = info.MessageCountDetails.DeadLetterMessageCount;
            long transferDeadLetterMessageCount = info.MessageCountDetails.TransferDeadLetterMessageCount;

            string destinationDeadLetterPath = EntityNameHelper.FormatDeadLetterPath(destination);
            string destinationTransferDeadLetterPath = EntityNameHelper.FormatTransferDeadLetterPath(destination);

            await client.CloseAsync();


            Console.WriteLine($"#'{activeMessageCount}' messages in '{destination}'");
            Console.WriteLine(
                $"#'{deadLetterMessageCount}' messages in '{destinationDeadLetterPath}'");
            Console.WriteLine(
                $"#'{transferDeadLetterMessageCount}' messages in '{destinationTransferDeadLetterPath}'");
        }
    }
}