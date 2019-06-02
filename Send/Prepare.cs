using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace Send
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            await client.DeleteQueueAsync(destination);
            await client.CloseAsync();
        }
    }
}