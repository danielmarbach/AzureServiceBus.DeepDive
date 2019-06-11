using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Batching
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string destination = "queue";

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var client = new QueueClient(connectionString, destination);
            var messages = new List<Message>();
            for (int i = 0; i < 10; i++)
            {
                var message = new Message();
                message.Body = Encoding.UTF8.GetBytes($"Deep Dive{i}");
                messages.Add(message);
            }
            await client.SendAsync(messages);
            messages.Clear();

            for (int i = 0; i < 6500; i++)
            {
                var message = new Message();
                message.Body = Encoding.UTF8.GetBytes($"Deep Dive{i}");
                messages.Add(message);
            }
            await client.SendAsync(messages);
        }
    }
}
