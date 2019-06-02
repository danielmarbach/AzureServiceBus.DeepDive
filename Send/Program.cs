using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Send
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string destination = "queue";

        static async Task Main(string[] args)
        {
            var client = new QueueClient(connectionString, destination);
            try
            {
                var message = new Message();
                message.Body = Encoding.UTF8.GetBytes("Deep Dive");

                message.UserProperties.Add("TenantId", "MyTenantId");
                // explore a few more properties

                await client.SendAsync(message);
            }
            finally
            {
                await client.CloseAsync();
            }
        }
    }
}
