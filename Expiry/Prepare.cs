using System;
using System.Threading.Tasks;

namespace Expiry
{
    using Azure.Messaging.ServiceBus;
    using Azure.Messaging.ServiceBus.Administration;

    public static class Prepare
    {
        public static async Task Stage(string connectionString, string destination)
        {
            var client = new ServiceBusAdministrationClient(connectionString);
            if (await client.QueueExistsAsync(destination)) await client.DeleteQueueAsync(destination);

            var description = new CreateQueueOptions(destination)
            {
                MaxDeliveryCount = int.MaxValue
            };
            await client.CreateQueueAsync(description);
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