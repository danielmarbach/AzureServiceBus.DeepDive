using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace AtomicSend
{
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);
            await client.CreateQueueAsync(destination);
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);

            QueueRuntimeProperties info = await client.GetQueueRuntimePropertiesAsync(destination);

            Console.WriteLine($"#'{info.ActiveMessageCount}' messages in '{destination}'");
        }
    }
}