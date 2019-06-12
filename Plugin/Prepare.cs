using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace Plugin
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(destination))
            {
                await client.DeleteQueueAsync(destination);
            }
            await client.CreateQueueAsync(destination);
            await client.CloseAsync();
        }
    }
}