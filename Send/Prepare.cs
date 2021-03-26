using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;

namespace Send
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            await client.DeleteQueueAsync(destination);
        }
    }
}