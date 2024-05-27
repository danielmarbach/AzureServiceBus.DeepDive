using System;
using System.Threading.Tasks;

namespace Batching
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task<IAsyncDisposable> Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (!await client.QueueExistsAsync(destination)) await client.CreateQueueAsync(new CreateQueueOptions(destination)
            {
                MaxMessageSizeInKilobytes = 1500
            });
            return new Leave(connectionString, destination);
        }

        static async Task LeaveStage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            await client.DeleteQueueAsync(destination);
        }

        sealed class Leave(string connectionString, string destination) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await LeaveStage(connectionString, destination);
            }
        }
    }
}