using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus.Management;

namespace AtomicSend
{
    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);
            await client.CreateQueueAsync(destination);
            await client.CloseAsync();
        }

        public static async Task ReportNumberOfMessages(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);

            var info = await client.GetQueueRuntimeInfoAsync(destination);

            await client.CloseAsync();

            Console.WriteLine($"#'{info.MessageCount}' messages in '{destination}'");
        }
    }
}