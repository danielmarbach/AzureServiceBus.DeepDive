using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Send
{
    class Program
    {
        static string connectionString = Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");
        static string destination = "queue";

        static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            Console.WriteLine("netstat -na | find \"5671\"");

            var sender = new MessageSender(connectionString, destination);
            await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Deep Dive")));
            var receiver = new MessageReceiver(connectionString, destination);
            await receiver.ReceiveAsync();

            Console.WriteLine("Continue with connection sharing");
            Console.ReadLine();

            await sender.CloseAsync();
            await receiver.CloseAsync();
            sender = null;
            receiver = null;

            GC.Collect();

            var connection = new ServiceBusConnection(connectionString);
            sender = new MessageSender(connection, destination);
            receiver = new MessageReceiver(connection, destination);

            await sender.SendAsync(new Message(Encoding.UTF8.GetBytes("Deep Dive")));
            await receiver.ReceiveAsync();

            Console.WriteLine("Enter to stop");
            Console.ReadLine();

            await sender.CloseAsync();
            await receiver.CloseAsync();

            await Prepare.LeaveStage(connectionString, destination);
        }
    }
}
