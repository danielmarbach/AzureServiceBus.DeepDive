using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Forwarding
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString);

            var client = new QueueClient(connectionString, "Hop4");
            var message = new Message();
            message.Body = Encoding.UTF8.GetBytes("Weeeeeeehhh!");
            await client.SendAsync(message);
            await client.CloseAsync();

            Console.WriteLine("Sent message");

            var hop = "Hop0";
            var receiver = new MessageReceiver(connectionString, hop);

            var receivedMessage = await receiver.ReceiveAsync();
            await receiver.CompleteAsync(receivedMessage.SystemProperties.LockToken);

            Console.WriteLine($"Got '{Encoding.UTF8.GetString(receivedMessage.Body)}' on hop '{hop}'");
            await receiver.CloseAsync();

            Console.WriteLine("Setup forwarding from Hop0 to Hop");
            Console.ReadLine();

            client = new QueueClient(connectionString, "Hop4");
            message = new Message();
            message.Body = Encoding.UTF8.GetBytes("Weeeeeeehhh!");
            await client.SendAsync(message);

            hop = "Hop0";
            receiver = new MessageReceiver(connectionString, hop);
            try
            {
                receivedMessage = await receiver.ReceiveAsync();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
            }
            finally
            {
                await receiver.CloseAsync();
            }
        }
    }
}
