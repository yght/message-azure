using System;
using System.Collections.Generic;
using System.Linq;

namespace MessagePlatform.Notifications.Core
{
    /// <summary>
    /// Decides whether, how and when to interrupt somebody.
    ///
    /// Pure. No Service Bus, no Cosmos, no clock of its own - the current time
    /// is a parameter. Everything that talks to Azure lives in the function
    /// that calls this.
    ///
    /// The ordering of the rules is the design. Mute beats presence, presence
    /// beats quiet hours, and urgency beats quiet hours but never beats mute.
    /// Somebody who muted a conversation has said what they want and an
    /// "urgent" flag set by another user is not permission to override it.
    /// </summary>
    public class NotificationPolicy
    {
        private readonly NotificationOptions _options;

        public NotificationPolicy(NotificationOptions options = null)
        {
            _options = options ?? new NotificationOptions();
        }

        public NotificationDecision Decide(MessageEvent message, RecipientState recipient, DateTime nowUtc)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (recipient == null) throw new ArgumentNullException(nameof(recipient));

            // You do not get notified about your own message. This looks
            // obvious and it happened anyway, because a group fan-out
            // included the sender in the recipient list.
            if (string.Equals(message.SenderId, recipient.UserId, StringComparison.Ordinal))
            {
                return Drop(DecisionReason.SelfMessage);
            }

            if (recipient.MutedConversationIds != null &&
                recipient.MutedConversationIds.Contains(message.ConversationId))
            {
                return Drop(DecisionReason.ConversationMuted);
            }

            // If they are looking at the app right now, the message is already
            // on their screen. A push notification for something visible is
            // the fastest way to get somebody to turn push off entirely.
            if (IsPresent(recipient, nowUtc))
            {
                return Drop(DecisionReason.RecipientPresent);
            }

            var channels = ChannelsFor(message, recipient);

            if (channels.Count == 0)
            {
                return Drop(DecisionReason.NoChannelsEnabled);
            }

            if (!message.IsUrgent && IsInQuietHours(recipient, nowUtc, out var quietEndsUtc))
            {
                return new NotificationDecision
                {
                    Channels = channels,
                    Deferred = true,
                    DeliverAfterUtc = quietEndsUtc,
                    Reason = DecisionReason.QuietHours
                };
            }

            // Group chatter is the main source of notification fatigue. Past
            // the threshold we stop sending individually and let the digest
            // pick it up.
            if (message.IsGroup &&
                !message.IsUrgent &&
                recipient.PendingDigestCount >= _options.DigestThreshold)
            {
                return new NotificationDecision
                {
                    Channels = channels,
                    Deferred = true,
                    DeliverAfterUtc = nowUtc.Add(_options.DigestWindow),
                    Reason = DecisionReason.DigestThresholdReached
                };
            }

            return new NotificationDecision
            {
                Channels = channels,
                Deferred = false,
                Reason = DecisionReason.Notify
            };
        }

        private static NotificationDecision Drop(DecisionReason reason)
        {
            return new NotificationDecision
            {
                Channels = new List<Channel>(),
                Deferred = false,
                Reason = reason
            };
        }

        private bool IsPresent(RecipientState recipient, DateTime nowUtc)
        {
            if (recipient.LastSeenUtc == null)
            {
                return false;
            }

            var since = nowUtc - recipient.LastSeenUtc.Value;

            // A heartbeat from the future means a client clock is wrong. Treat
            // it as present rather than as an enormous negative age, which
            // would otherwise wrap the comparison and mark them absent.
            if (since < TimeSpan.Zero)
            {
                return true;
            }

            return since <= _options.PresenceWindow;
        }

        private List<Channel> ChannelsFor(MessageEvent message, RecipientState recipient)
        {
            var enabled = recipient.EnabledChannels ?? new HashSet<Channel>();
            var chosen = new List<Channel>();

            if (enabled.Contains(Channel.Push))
            {
                chosen.Add(Channel.Push);
            }
            else if (enabled.Contains(Channel.Email))
            {
                // Email is the fallback for someone with no device registered,
                // not an addition. Sending both is how you get complaints.
                chosen.Add(Channel.Email);
            }

            // SMS costs money per message and wakes people up. Urgent only,
            // and only when they have opted into it.
            if (message.IsUrgent && enabled.Contains(Channel.Sms))
            {
                chosen.Add(Channel.Sms);
            }

            return chosen;
        }

        /// <summary>
        /// Quiet hours are wall-clock local, so this converts rather than
        /// comparing UTC. A window that wraps midnight (22:00 to 07:00) is the
        /// normal case, not the edge case.
        /// </summary>
        internal bool IsInQuietHours(RecipientState recipient, DateTime nowUtc, out DateTime? endsUtc)
        {
            endsUtc = null;

            if (recipient.QuietHoursStart == null || recipient.QuietHoursEnd == null)
            {
                return false;
            }

            var zone = ResolveZone(recipient.TimeZoneId);
            if (zone == null)
            {
                // An unknown zone must not silence somebody's notifications
                // forever. Fail open: send it.
                return false;
            }

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);

            var start = recipient.QuietHoursStart.Value;
            var end = recipient.QuietHoursEnd.Value;
            var timeOfDay = localNow.TimeOfDay;

            bool inWindow;
            DateTime localEnd;

            if (start <= end)
            {
                inWindow = timeOfDay >= start && timeOfDay < end;
                localEnd = localNow.Date.Add(end);
            }
            else
            {
                // Wraps midnight.
                inWindow = timeOfDay >= start || timeOfDay < end;
                localEnd = timeOfDay >= start
                    ? localNow.Date.AddDays(1).Add(end)
                    : localNow.Date.Add(end);
            }

            if (!inWindow)
            {
                return false;
            }

            endsUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), zone);

            return true;
        }

        private static TimeZoneInfo ResolveZone(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                return null;
            }
            catch (InvalidTimeZoneException)
            {
                return null;
            }
        }
    }

    public class NotificationOptions
    {
        /// <summary>
        /// How recently we must have heard from a client to call them present.
        /// The web client heartbeats every 30 seconds, so this is two beats.
        /// </summary>
        public TimeSpan PresenceWindow { get; set; } = TimeSpan.FromSeconds(60);

        public int DigestThreshold { get; set; } = 5;

        public TimeSpan DigestWindow { get; set; } = TimeSpan.FromMinutes(15);
    }
}
