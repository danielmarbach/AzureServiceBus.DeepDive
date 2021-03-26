using System;
using System.Threading.Tasks;

namespace Dedup
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);

            var queueDescription = new CreateQueueOptions(destination)
            {
                RequiresDuplicateDetection = true,
                DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20)
            };
            await client.CreateQueueAsync(queueDescription);
        }
    }
}