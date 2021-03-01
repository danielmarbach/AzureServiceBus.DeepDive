using System;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Management;

namespace Expiry
{
    using System.Threading;
    using Azure.Messaging.ServiceBus;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ManagementClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);

            var description = new QueueDescription(destination)
            {
                MaxDeliveryCount = int.MaxValue
            };
            await client.CreateQueueAsync(description);
            await client.CloseAsync();
        }

        public static async Task SimulateActiveReceiver(ServiceBusClient serviceBusClient, string destination)
        {
            await using var receiver = serviceBusClient.CreateProcessor(destination, new ServiceBusProcessorOptions { AutoCompleteMessages = false });
            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                await processMessageEventArgs.AbandonMessageAsync(processMessageEventArgs.Message);
                await Console.Error.WriteAsync(".");
                await Task.Delay(2000, processMessageEventArgs.CancellationToken);
            };
            receiver.ProcessErrorAsync += _ => Task.CompletedTask;

            await receiver.StartProcessingAsync();

            await Task.Delay(TimeSpan.FromSeconds(11));

            await receiver.StopProcessingAsync();
        }
    }
}