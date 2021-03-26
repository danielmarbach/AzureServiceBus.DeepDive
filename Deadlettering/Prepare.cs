using System.Threading.Tasks;

namespace Deadlettering
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);

            var description = new CreateQueueOptions(destination)
            {
                DeadLetteringOnMessageExpiration = true, // default false
                MaxDeliveryCount = 1
            };
            await client.CreateQueueAsync(description);
        }
    }
}