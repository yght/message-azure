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

        // ----------------------------------------------------------- quiet hours

        private static RecipientState Sleeper(Action<RecipientState>? tweak = null)
        {
            return Recipient(r =>
            {
                r.QuietHoursStart = new TimeSpan(22, 0, 0);
                r.QuietHoursEnd = new TimeSpan(7, 0, 0);
                tweak?.Invoke(r);
            });
        }

        [Fact]
        public void DefersInsideQuietHoursThatWrapMidnight()
        {
            // 03:00 in Toronto on 15 June is 07:00 UTC.
            var threeAm = new DateTime(2021, 6, 15, 7, 0, 0, DateTimeKind.Utc);
            var decision = _policy.Decide(Message(), Sleeper(), threeAm);

            Assert.True(decision.Deferred);
            Assert.Equal(DecisionReason.QuietHours, decision.Reason);
            Assert.False(decision.WillSendNow);
        }

        [Fact]
        public void DefersUntilQuietHoursEndInTheUsersOwnMorning()
        {
            var threeAm = new DateTime(2021, 6, 15, 7, 0, 0, DateTimeKind.Utc);
            var decision = _policy.Decide(Message(), Sleeper(), threeAm);

            // 07:00 Toronto on the 15th is 11:00 UTC, EDT being UTC-4.
            Assert.Equal(new DateTime(2021, 6, 15, 11, 0, 0, DateTimeKind.Utc), decision.DeliverAfterUtc);
        }

        [Fact]
        public void DefersToTomorrowMorningWhenItIsLateEvening()
        {
            // 23:00 Toronto on 15 June is 03:00 UTC on the 16th.
            var elevenPm = new DateTime(2021, 6, 16, 3, 0, 0, DateTimeKind.Utc);
            var decision = _policy.Decide(Message(), Sleeper(), elevenPm);

            Assert.True(decision.Deferred);
            // Still the 16th locally in the morning: 07:00 Toronto = 11:00 UTC.
            Assert.Equal(new DateTime(2021, 6, 16, 11, 0, 0, DateTimeKind.Utc), decision.DeliverAfterUtc);
        }

        [Fact]
        public void SendsOutsideQuietHours()
        {
            // Midday Toronto.
            var decision = _policy.Decide(Message(), Sleeper(), Midday);

            Assert.False(decision.Deferred);
            Assert.Equal(DecisionReason.Notify, decision.Reason);
        }

        [Fact]
        public void UrgentBreaksThroughQuietHours()
        {
            var threeAm = new DateTime(2021, 6, 15, 7, 0, 0, DateTimeKind.Utc);
            var decision = _policy.Decide(Message(urgent: true), Sleeper(), threeAm);

            Assert.True(decision.WillSendNow);
        }

        [Fact]
        public void HandlesAQuietWindowThatDoesNotWrapMidnight()
        {
            // A day-sleeper: quiet from 09:00 to 17:00 local.
            var dayShift = Recipient(r =>
            {
                r.QuietHoursStart = new TimeSpan(9, 0, 0);
                r.QuietHoursEnd = new TimeSpan(17, 0, 0);
            });

            var decision = _policy.Decide(Message(), dayShift, Midday); // midday Toronto

            Assert.True(decision.Deferred);
            Assert.Equal(new DateTime(2021, 6, 15, 21, 0, 0, DateTimeKind.Utc), decision.DeliverAfterUtc);
        }

        [Fact]
        public void RespectsTheUsersZoneRatherThanTheServers()
        {
            // 03:00 UTC is 04:00 in London and 23:00 the previous day in
            // Toronto. Both are inside a 22:00-07:00 window, and a naive UTC
            // comparison would get one of them wrong.
            var at3Utc = new DateTime(2021, 6, 15, 3, 0, 0, DateTimeKind.Utc);

            var london = Sleeper(r => r.TimeZoneId = "Europe/London");
            var toronto = Sleeper();

            Assert.True(_policy.Decide(Message(), london, at3Utc).Deferred);
            Assert.True(_policy.Decide(Message(), toronto, at3Utc).Deferred);
        }

        [Fact]
        public void SendsRatherThanSilencingWhenTheTimeZoneIsUnknown()
        {
            // Failing closed here would silence somebody indefinitely because
            // of a bad string in their profile.
            var broken = Sleeper(r => r.TimeZoneId = "Mars/Olympus_Mons");
            var threeAm = new DateTime(2021, 6, 15, 7, 0, 0, DateTimeKind.Utc);

            var decision = _policy.Decide(Message(), broken, threeAm);

            Assert.False(decision.Deferred);
            Assert.Equal(DecisionReason.Notify, decision.Reason);
        }

        [Fact]
        public void IgnoresQuietHoursWhenTheUserHasNotSetThem()
        {
            var threeAm = new DateTime(2021, 6, 15, 7, 0, 0, DateTimeKind.Utc);
            var decision = _policy.Decide(Message(), Recipient(), threeAm);

            Assert.Equal(DecisionReason.Notify, decision.Reason);
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
