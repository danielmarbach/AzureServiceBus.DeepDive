using System;
using System.Threading.Tasks;

namespace Scheduling
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task<IAsyncDisposable> Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (!await client.QueueExistsAsync(destination)) await client.CreateQueueAsync(destination);
            return new Leave(connectionString, destination);
        }

        static async Task LeaveStage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            await client.DeleteQueueAsync(destination);
        }

        sealed class Leave : IAsyncDisposable
        {
            readonly string connectionString;
            readonly string destination;

            public Leave(string connectionString, string destination)
            {
                this.connectionString = connectionString;
                this.destination = destination;
            }

            public async ValueTask DisposeAsync()
            {
                await LeaveStage(connectionString, destination);
            }
        }
    }
}