using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
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
            try
            {
                await client.SendAsync(messages);
            }
            catch (MessageSizeExceededException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

            messages.Clear();
            Console.WriteLine();

            for (int i = 0; i < 101; i++)
            {
                var message = new Message();
                message.Body = Encoding.UTF8.GetBytes($"Deep Dive{i}");
                messages.Add(message);
            }

            try
            {
                using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    await client.SendAsync(messages);
                    scope.Complete();
                }
            }
            catch (QuotaExceededException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }

        }
    }
}
