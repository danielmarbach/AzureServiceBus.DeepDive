using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace Session
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);
            var queueDescription = new QueueDescription(destination)
            {
                RequiresSession = true
            };
            await client.CreateQueueAsync(queueDescription);
            await client.CloseAsync();
        }
    }
}