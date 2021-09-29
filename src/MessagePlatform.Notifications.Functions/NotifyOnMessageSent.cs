using System;
using System.Threading.Tasks;
using MessagePlatform.Notifications.Core;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace MessagePlatform.Notifications.Functions
{
    /// <summary>
    /// Service Bus trigger on message-sent.
    ///
    /// This is the shell. Every decision worth arguing about lives in
    /// NotificationPolicy, which is pure and covered by tests; this file
    /// fetches state, calls it, and does what it says.
    /// </summary>
    public class NotifyOnMessageSent
    {
        private readonly IRecipientStore _recipients;
        private readonly INotificationSender _sender;
        private readonly NotificationPolicy _policy;

        public NotifyOnMessageSent(
            IRecipientStore recipients,
            INotificationSender sender,
            NotificationPolicy policy)
        {
            _recipients = recipients;
            _sender = sender;
            _policy = policy;
        }

        [FunctionName("NotifyOnMessageSent")]
        public async Task Run(
            [ServiceBusTrigger("message-sent", "notifications", Connection = "ServiceBusConnection")]
            string body,
            string messageId,
            int deliveryCount,
            ILogger log)
        {
            var evt = JsonConvert.DeserializeObject<MessageEvent>(body);

            if (evt == null || string.IsNullOrEmpty(evt.RecipientId))
            {
                /*
                  A malformed message will be malformed on every redelivery.
                  Returning normally completes it and lets it go, rather than
                  burning five delivery attempts to reach the dead letter
                  queue with something nobody can act on.
                */
                log.LogError("Unparseable message {MessageId}, discarding", messageId);
                return;
            }

            var recipient = await _recipients.GetAsync(evt.RecipientId);

            if (recipient == null)
            {
                log.LogWarning("No recipient state for {UserId}", evt.RecipientId);
                return;
            }

            var decision = _policy.Decide(evt, recipient, DateTime.UtcNow);

            log.LogInformation(
                "Message {MessageId} for {UserId}: {Reason}, channels {Channels}, deferred {Deferred}",
                evt.MessageId, evt.RecipientId, decision.Reason,
                string.Join(",", decision.Channels), decision.Deferred);

            if (!decision.WillSendNow)
            {
                return;
            }

            foreach (var channel in decision.Channels)
            {
                /*
                  Idempotency key is (message, recipient, channel). Service Bus
                  is at-least-once, and deliveryCount above one means we have
                  been here before - the sender uses this key to avoid a second
                  push for the same message.
                */
                await _sender.SendAsync(channel, evt, recipient,
                    idempotencyKey: $"{evt.MessageId}:{evt.RecipientId}:{channel}");
            }
        }
    }

    public interface IRecipientStore
    {
        Task<RecipientState> GetAsync(string userId);
    }

    public interface INotificationSender
    {
        Task SendAsync(Channel channel, MessageEvent message, RecipientState recipient, string idempotencyKey);
    }
}
