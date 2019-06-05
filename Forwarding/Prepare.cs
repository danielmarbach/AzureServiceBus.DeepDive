using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace Fowarding
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString)
        {
            var client = new ManagementClient(connectionString);

            async Task DeleteIfExists(string queueName)
            {
                if (await client.QueueExistsAsync(queueName))
                {
                    await client.DeleteQueueAsync(queueName);
                }
            }

            await Task.WhenAll(
                DeleteIfExists("Hop4"), 
                DeleteIfExists("Hop3"),
                DeleteIfExists("Hop2"),
                DeleteIfExists("Hop1"),
                DeleteIfExists("Hop0"),
                DeleteIfExists("Hop")
            );

            var description = new QueueDescription("Hop")
            {
            };
            await client.CreateQueueAsync(description);

            description = new QueueDescription("Hop0")
            {
            };
            await client.CreateQueueAsync(description);

            description = new QueueDescription("Hop1")
            {
                ForwardTo = "Hop0"
            };
            await client.CreateQueueAsync(description);

            description = new QueueDescription("Hop2")
            {
                ForwardTo = "Hop1"
            };
            await client.CreateQueueAsync(description);

            description = new QueueDescription("Hop3")
            {
                ForwardTo = "Hop2"
            };
            await client.CreateQueueAsync(description);

            description = new QueueDescription("Hop4")
            {
                ForwardTo = "Hop3"
            };
            await client.CreateQueueAsync(description);

            await client.CloseAsync();
        }
    }
}