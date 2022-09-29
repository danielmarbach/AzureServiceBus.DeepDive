using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;

namespace SendVia
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string inputQueue, string destinationQueue, string anotherDestinationQueue)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new CreateQueueOptions(inputQueue) { MaxDeliveryCount = 2 });

            if (await client.QueueExistsAsync(destinationQueue)) await client.DeleteQueueAsync(destinationQueue);
            if (await client.QueueExistsAsync(anotherDestinationQueue)) await client.DeleteQueueAsync(anotherDestinationQueue);
            await client.CreateQueueAsync(destinationQueue);
            await client.CreateQueueAsync(anotherDestinationQueue);
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination, string anotherDestination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            QueueRuntimeProperties info = await client.GetQueueRuntimePropertiesAsync(destination);
            QueueRuntimeProperties anotherInfo = await client.GetQueueRuntimePropertiesAsync(anotherDestination);

            Console.WriteLine($"#'{info.ActiveMessageCount}' messages in '{destination}'");
            Console.WriteLine($"#'{anotherInfo.ActiveMessageCount}' messages in '{anotherDestination}'");
        }
    }
}