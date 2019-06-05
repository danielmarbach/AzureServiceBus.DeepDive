using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace Expiry
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string destination = "queue";

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);

            var message = new Message();
            message.Body = Encoding.UTF8.GetBytes("Half life");
            // if not set the default time to live on the queue counts
            message.TimeToLive = TimeSpan.FromSeconds(10);

            await client.SendAsync(message);

            // Note that expired messages are only purged and moved to the DLQ when there is at least one
            // active receiver pulling from the main queue or subscription; that behavior is by design.
            await Prepare.SimulateActiveReceiver(client);

            await client.CloseAsync();
        }
    }
}
