using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;

namespace SendVia
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string inputQueue, string destinationQueue, string errorQueue)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new CreateQueueOptions(inputQueue)
            {
                MaxDeliveryCount = 1,
                LockDuration = TimeSpan.FromMinutes(5)
            });

            if (await client.QueueExistsAsync(destinationQueue)) await client.DeleteQueueAsync(destinationQueue);
            await client.CreateQueueAsync(destinationQueue);

            if (await client.QueueExistsAsync(errorQueue)) await client.DeleteQueueAsync(errorQueue);
            await client.CreateQueueAsync(errorQueue);
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            QueueRuntimeProperties info = await client.GetQueueRuntimePropertiesAsync(destination);

            Console.WriteLine($"#'{info.ActiveMessageCount}' messages in '{destination}'");
            Console.WriteLine($"#'{info.TransferMessageCount}' transfer messages in '{destination}'");
        }
    }
}