using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace Connections
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            if (!await client.QueueExistsAsync(destination))
            {
                await client.CreateQueueAsync(destination);
            }
            await client.CloseAsync();
        }

        public static async Task LeaveStage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            await client.DeleteQueueAsync(destination);
            await client.CloseAsync();
        }
    }
}