using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Expiry
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);

            var message = new Message();
            message.Body = Encoding.UTF8.GetBytes("Half life");
            // if not set the default time to live on the queue counts
            message.TimeToLive = TimeSpan.FromSeconds(10);

            await client.SendAsync(message);
            Console.WriteLine("Sent message");

            // Note that expired messages are only purged and moved to the DLQ when there is at least one
            // active receiver pulling from the main queue or subscription; that behavior is by design.
            await Prepare.SimulateActiveReceiver(client);

            Console.WriteLine("Message expired");

            await client.CloseAsync();
        }
    }
}