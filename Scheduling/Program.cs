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

            var sender = new MessageSender(connectionString, destination);
            var due = DateTimeOffset.UtcNow.AddSeconds(10);
            await sender.ScheduleMessageAsync(new Message(Encoding.UTF8.GetBytes($"Deep Dive + {due}")), due);
            Console.WriteLine("Message scheduled first");

            var sequenceId = await sender.ScheduleMessageAsync(new Message(Encoding.UTF8.GetBytes($"Deep Dive + {due}")), due);
            Console.WriteLine("Message scheduled second");

            await sender.CancelScheduledMessageAsync(sequenceId);
            Console.WriteLine("Canceled second");

            var receiver = new MessageReceiver(connectionString, destination);
            receiver.RegisterMessageHandler((message, token) => {

                Console.WriteLine($"Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}' at '{DateTimeOffset.UtcNow}'");
                return Task.CompletedTask;
            }, ex => Task.CompletedTask);


            await Task.Delay(TimeSpan.FromSeconds(20));
            await sender.CloseAsync();
            await receiver.CloseAsync();

            await Prepare.LeaveStage(connectionString, destination);
        }
    }
}
