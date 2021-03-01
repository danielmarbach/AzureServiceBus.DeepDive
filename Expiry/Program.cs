﻿using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;

namespace Expiry
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString);

            await using var sender = serviceBusClient.CreateSender(destination);

            var message = new ServiceBusMessage("Half life")
            {
                // if not set the default time to live on the queue counts
                TimeToLive = TimeSpan.FromSeconds(10)
            };

            await sender.SendMessageAsync(message);
            Console.WriteLine("Sent message");

            // Note that expired messages are only purged and moved to the DLQ when there is at least one
            // active receiver pulling from the main queue or subscription; that behavior is by design.
            await Prepare.SimulateActiveReceiver(serviceBusClient, destination);

            Console.WriteLine("Message expired");
        }
    }
}