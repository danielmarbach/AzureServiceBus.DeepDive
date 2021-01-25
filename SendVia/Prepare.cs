using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace SendVia
{
    public static class Prepare
    {
        public static MessageHandlerOptions Options(string connectionString, string destinationQueue, string anotherDestinationQueue) => new MessageHandlerOptions(
                    async exception =>
                    {
                        Console.WriteLine("Failed." + exception.Exception.Message);
                        await Prepare.ReportNumberOfMessages(connectionString, destinationQueue);
                        await Prepare.ReportNumberOfMessages(connectionString, anotherDestinationQueue);
                    })
        {
            AutoComplete = false,
            MaxConcurrentCalls = 1,
            MaxAutoRenewDuration = TimeSpan.FromMinutes(10)
        };

        public static async Task Stage(string connectionString, string inputQueue, string destinationQueue, string anotherDestinationQueue)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(inputQueue)) await client.DeleteQueueAsync(inputQueue);
            await client.CreateQueueAsync(new QueueDescription(inputQueue) { MaxDeliveryCount = 2 });

            if (await client.QueueExistsAsync(destinationQueue)) await client.DeleteQueueAsync(destinationQueue);
            await client.CreateQueueAsync(destinationQueue);
            if (await client.QueueExistsAsync(anotherDestinationQueue)) await client.DeleteQueueAsync(anotherDestinationQueue);
            await client.CreateQueueAsync(anotherDestinationQueue);

            await client.CloseAsync();
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);

            var info = await client.GetQueueRuntimeInfoAsync(destination);

            await client.CloseAsync();

            Console.WriteLine($"#'{info.MessageCount}' messages in '{destination}'");
        }
    }
}