using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Session
{
    using Azure.Messaging.ServiceBus;

    internal class Program
    {
        private static readonly string connectionString =
            Environment.GetEnvironmentVariable("AzureServiceBus_ConnectionString");

        private static readonly string destination = "queue";

        private static TaskCompletionSource<bool> syncEvent =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task Main(string[] args)
        {
            await Prepare.Stage(connectionString, destination);

            await using var serviceBusClient = new ServiceBusClient(connectionString, new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    TryTimeout = TimeSpan.FromSeconds(2)
                }
            });
            await using var sender = serviceBusClient.CreateSender(destination);

            var messages = new List<ServiceBusMessage>
            {
                new ServiceBusMessage("Orange 1") {SessionId = "Orange"},
                new ServiceBusMessage("Green 1") {SessionId = "Green"},
                new ServiceBusMessage("Blue 1") {SessionId = "Blue"},
                new ServiceBusMessage("Green 2") {SessionId = "Green"},
                new ServiceBusMessage("Orange 2") {SessionId = "Orange"},
                new ServiceBusMessage("Blue 2") {SessionId = "Blue"},
                new ServiceBusMessage("Green 3") {SessionId = "Green"},
                new ServiceBusMessage("Orange 3") {SessionId = "Orange"},
                new ServiceBusMessage("Green 4") {SessionId = "Green"},
                new ServiceBusMessage("Purple 1") {SessionId = "Purple"},
                new ServiceBusMessage("Blue 3") {SessionId = "Blue"},
                new ServiceBusMessage("Orange 4") {SessionId = "Orange"}
            };

            await sender.SendMessagesAsync(messages);

            Console.WriteLine("Messages sent");

            await using var receiver = serviceBusClient.CreateSessionProcessor(destination, new ServiceBusSessionProcessorOptions
            {
                AutoCompleteMessages = true,
                MaxConcurrentSessions = 1,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(10),
                SessionIdleTimeout = TimeSpan.FromSeconds(2)
            });
            receiver.ProcessMessageAsync += async processMessageEventArgs =>
            {
                var message = processMessageEventArgs.Message;

                await Console.Error.WriteLineAsync(
                    $"Received message on session '{processMessageEventArgs.SessionId}' with '{message.MessageId}' and content '{Encoding.UTF8.GetString(message.Body)}'");
            };
            receiver.ProcessErrorAsync += async processErrorEventArgs =>
            {
                await Console.Error.WriteLineAsync($"Exception: {processErrorEventArgs.Exception}");
                await Console.Error.WriteLineAsync($"FullyQualifiedNamespace: {processErrorEventArgs.FullyQualifiedNamespace}");
                await Console.Error.WriteLineAsync($"ErrorSource: {processErrorEventArgs.ErrorSource}");
                await Console.Error.WriteLineAsync($"EntityPath: {processErrorEventArgs.EntityPath}");
            };

            await receiver.StartProcessingAsync();

            Console.ReadLine();

            await receiver.StopProcessingAsync();
        }
    }
}