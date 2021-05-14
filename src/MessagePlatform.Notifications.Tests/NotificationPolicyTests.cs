using System;
using System.Collections.Generic;
using System.Linq;
using MessagePlatform.Notifications.Core;
using Xunit;

namespace MessagePlatform.Notifications.Tests
{
    public class NotificationPolicyTests
    {
        private const string Me = "user_dana";
        private const string Them = "user_yousof";
        private const string Conversation = "conv_4821";
        private const string Toronto = "America/Toronto";

        private static readonly DateTime Midday = new DateTime(2021, 6, 15, 16, 0, 0, DateTimeKind.Utc);

        private readonly NotificationPolicy _policy = new NotificationPolicy();

        private static MessageEvent Message(bool urgent = false, bool group = false, string sender = Them)
        {
            return new MessageEvent
            {
                MessageId = "m1",
                ConversationId = Conversation,
                SenderId = sender,
                RecipientId = Me,
                SentAtUtc = Midday,
                IsUrgent = urgent,
                IsGroup = group
            };
        }

        private static RecipientState Recipient(Action<RecipientState>? tweak = null)
        {
            var state = new RecipientState
            {
                UserId = Me,
                TimeZoneId = Toronto,
                LastSeenUtc = null,
                EnabledChannels = new HashSet<Channel> { Channel.Push, Channel.Email }
            };

            tweak?.Invoke(state);
            return state;
        }

        // ---------------------------------------------------------------- drops

        [Fact]
        public void DoesNotNotifyYouAboutYourOwnMessage()
        {
            var decision = _policy.Decide(Message(sender: Me), Recipient(), Midday);

            Assert.Equal(DecisionReason.SelfMessage, decision.Reason);
            Assert.Empty(decision.Channels);
            Assert.False(decision.Deferred);
        }

        [Fact]
        public void DoesNotNotifyForAMutedConversation()
        {
            var recipient = Recipient(r => r.MutedConversationIds = new HashSet<string> { Conversation });
            var decision = _policy.Decide(Message(), recipient, Midday);

            Assert.Equal(DecisionReason.ConversationMuted, decision.Reason);
            Assert.False(decision.WillSendNow);
        }

        [Fact]
        public void UrgencyDoesNotOverrideMute()
        {
            // Someone else setting a flag is not permission to override what
            // this user explicitly asked for.
            var recipient = Recipient(r => r.MutedConversationIds = new HashSet<string> { Conversation });
            var decision = _policy.Decide(Message(urgent: true), recipient, Midday);

            Assert.Equal(DecisionReason.ConversationMuted, decision.Reason);
        }

        [Fact]
        public void DoesNotPushToSomebodyWhoIsLookingAtTheApp()
        {
            var recipient = Recipient(r => r.LastSeenUtc = Midday.AddSeconds(-10));
            var decision = _policy.Decide(Message(), recipient, Midday);

            Assert.Equal(DecisionReason.RecipientPresent, decision.Reason);
        }

        [Fact]
        public void NotifiesOnceThePresenceWindowHasLapsed()
        {
            var recipient = Recipient(r => r.LastSeenUtc = Midday.AddSeconds(-61));
            var decision = _policy.Decide(Message(), recipient, Midday);

            Assert.Equal(DecisionReason.Notify, decision.Reason);
        }

        [Fact]
        public void TreatsAHeartbeatFromTheFutureAsPresent()
        {
            // A client with a fast clock. Subtracting gives a negative age,
            // which must not read as "seen a very long time ago".
            var recipient = Recipient(r => r.LastSeenUtc = Midday.AddMinutes(5));
            var decision = _policy.Decide(Message(), recipient, Midday);

            Assert.Equal(DecisionReason.RecipientPresent, decision.Reason);
        }

        [Fact]
        public void NotifiesSomebodyWeHaveNeverSeen()
        {
            var decision = _policy.Decide(Message(), Recipient(r => r.LastSeenUtc = null), Midday);
            Assert.Equal(DecisionReason.Notify, decision.Reason);
        }

        // -------------------------------------------------------------- channels

        [Fact]
        public void PrefersPushWhenItIsAvailable()
        {
            var decision = _policy.Decide(Message(), Recipient(), Midday);
            Assert.Equal(new[] { Channel.Push }, decision.Channels);
        }

        [Fact]
        public void FallsBackToEmailWhenPushIsOff()
        {
            var recipient = Recipient(r => r.EnabledChannels = new HashSet<Channel> { Channel.Email });
            var decision = _policy.Decide(Message(), recipient, Midday);

            Assert.Equal(new[] { Channel.Email }, decision.Channels);
        }

        [Fact]
        public void DoesNotSendPushAndEmailForTheSameMessage()
        {
            var decision = _policy.Decide(Message(), Recipient(), Midday);
            Assert.DoesNotContain(Channel.Email, decision.Channels);
        }

        [Fact]
        public void AddsSmsOnlyWhenUrgentAndOptedIn()
        {
            var optedIn = Recipient(r => r.EnabledChannels =
                new HashSet<Channel> { Channel.Push, Channel.Sms });

            var routine = _policy.Decide(Message(), optedIn, Midday);
            var urgent = _policy.Decide(Message(urgent: true), optedIn, Midday);

            Assert.DoesNotContain(Channel.Sms, routine.Channels);
            Assert.Contains(Channel.Sms, urgent.Channels);
        }

        [Fact]
        public void DoesNotSendSmsToSomebodyWhoDidNotOptIn()
        {
            var decision = _policy.Decide(Message(urgent: true), Recipient(), Midday);
            Assert.DoesNotContain(Channel.Sms, decision.Channels);
        }

        [Fact]
        public void DropsWhenEveryChannelIsDisabled()
        {
            var recipient = Recipient(r => r.EnabledChannels = new HashSet<Channel>());
            var decision = _policy.Decide(Message(), recipient, Midday);

            Assert.Equal(DecisionReason.NoChannelsEnabled, decision.Reason);
        }

        // ------------------------------------------------------------ arguments

        [Fact]
        public void RejectsNullArguments()
        {
            Assert.Throws<ArgumentNullException>(() => _policy.Decide(null!, Recipient(), Midday));
            Assert.Throws<ArgumentNullException>(() => _policy.Decide(Message(), null!, Midday));
        }
    }
}
