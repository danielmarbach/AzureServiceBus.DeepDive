using System;
using System.Text;
using System.Threading.Tasks;

namespace Forwarding
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString);

            await using var serviceBusClient = new ServiceBusClient(connectionString);
            var sender = serviceBusClient.CreateSender("Hop4");

            var message = new ServiceBusMessage("Weeeeeeehhh!");
            await sender.SendMessageAsync(message);
            await sender.CloseAsync();

            Console.WriteLine("Sent message");

            var hop = "Hop0";
            var receiver = serviceBusClient.CreateReceiver(hop);

            var receivedMessage = await receiver.ReceiveMessageAsync();
            await receiver.CompleteMessageAsync(receivedMessage);

            Console.WriteLine($"Got '{Encoding.UTF8.GetString(receivedMessage.Body)}' on hop '{hop}'");
            await receiver.CloseAsync();

            Console.WriteLine("Setup forwarding from Hop0 to Hop");
            Console.ReadLine();

            sender = serviceBusClient.CreateSender("Hop4");
            message = new ServiceBusMessage("Weeeeeeehhh!");
            await sender.SendMessageAsync(message);

            hop = "Hop0";
            receiver = serviceBusClient.CreateReceiver(hop);
            try
            {
                receivedMessage = await receiver.ReceiveMessageAsync();
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
            }
            finally
            {
                await receiver.CloseAsync();
            }
        }
    }
}