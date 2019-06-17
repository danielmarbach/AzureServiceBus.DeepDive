using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Plugin
{
    class MyCustomPlugin : ServiceBusPlugin
    {
        public override string Name => "MyCustomPlugin";

        public override Task<Message> BeforeMessageSend(Message message)
        {
            var currentBody = Encoding.UTF8.GetString(message.Body);
            message.Body = Encoding.UTF8.GetBytes($"Yes I can before send{Environment.NewLine}{currentBody}");
            return Task.FromResult(message);
        }

        public override Task<Message> AfterMessageReceive(Message message)
        {
            var currentBody = Encoding.UTF8.GetString(message.Body);
            message.Body = Encoding.UTF8.GetBytes($"{currentBody}{Environment.NewLine}Yes I can before receive");
            return Task.FromResult(message);
        }
    }
}