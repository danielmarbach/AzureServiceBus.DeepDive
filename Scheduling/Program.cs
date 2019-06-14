using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Scheduling
{
    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            var sender = new MessageSender(connectionString, destination);
            var due = DateTimeOffset.UtcNow.AddSeconds(10);
            await sender.ScheduleMessageAsync(new Message(Encoding.UTF8.GetBytes($"Deep Dive + {due}")), due);
            Console.WriteLine($"{DateTimeOffset.UtcNow}: Message scheduled first");

            var sequenceId =
                await sender.ScheduleMessageAsync(new Message(Encoding.UTF8.GetBytes($"Deep Dive + {due}")), due);
            Console.WriteLine($"{DateTimeOffset.UtcNow}: Message scheduled second");

            await sender.CancelScheduledMessageAsync(sequenceId);
            Console.WriteLine($"{DateTimeOffset.UtcNow}: Canceled second");

            var receiver = new MessageReceiver(connectionString, destination);
            receiver.RegisterMessageHandler((message, token) =>
            {
                Console.WriteLine(
                    $"{DateTimeOffset.UtcNow}: Received message with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
                return Task.CompletedTask;
            }, ex => Task.CompletedTask);


            await Task.Delay(TimeSpan.FromSeconds(20));
            await sender.CloseAsync();
            await receiver.CloseAsync();

            await Prepare.LeaveStage(connectionString, destination);
        }
    }
}