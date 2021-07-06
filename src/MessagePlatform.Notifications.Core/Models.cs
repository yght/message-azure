using System;
using System.Collections.Generic;

namespace MessagePlatform.Notifications.Core
{
    /// <summary>
    /// A message that has just been stored, arriving on the Service Bus topic.
    /// </summary>
    public class MessageEvent
    {
        public string MessageId { get; set; }
        public string ConversationId { get; set; }
        public string SenderId { get; set; }
        public string RecipientId { get; set; }
        public DateTime SentAtUtc { get; set; }
        public bool IsUrgent { get; set; }
        public bool IsGroup { get; set; }
    }

    public enum Channel
    {
        Push,
        Email,
        Sms
    }

    /// <summary>
    /// What we know about the person we might be about to interrupt.
    /// </summary>
    public class RecipientState
    {
        public string UserId { get; set; }

        /// <summary>
        /// IANA identifier, e.g. "America/Toronto". Quiet hours are meaningless
        /// without it, and users travel.
        /// </summary>
        public string TimeZoneId { get; set; }

        /// <summary>
        /// Last presence heartbeat. Null means we have never seen them.
        /// </summary>
        public DateTime? LastSeenUtc { get; set; }

        public ISet<string> MutedConversationIds { get; set; } = new HashSet<string>();

        public ISet<Channel> EnabledChannels { get; set; } =
            new HashSet<Channel> { Channel.Push, Channel.Email };

        /// <summary>
        /// Local wall-clock hours during which we do not interrupt. Both null
        /// means the user has not set any.
        /// </summary>
        public TimeSpan? QuietHoursStart { get; set; }
        public TimeSpan? QuietHoursEnd { get; set; }
    }

    public enum DecisionReason
    {
        Notify,
        SelfMessage,
        ConversationMuted,
        RecipientPresent,
        QuietHours,
        NoChannelsEnabled
    }

    public class NotificationDecision
    {
        public IReadOnlyList<Channel> Channels { get; set; } = new List<Channel>();

        /// <summary>
        /// True when we are holding this back rather than dropping it. The
        /// difference matters: dropped means the user never hears about it,
        /// deferred means they hear about it later.
        /// </summary>
        public bool Deferred { get; set; }

        public DateTime? DeliverAfterUtc { get; set; }

        public DecisionReason Reason { get; set; }

        public bool WillSendNow => !Deferred && Channels.Count > 0;
    }
}
