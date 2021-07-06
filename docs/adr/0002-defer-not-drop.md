# 2. Quiet hours defer; mute drops

Date: 2021-07-06
Status: Accepted

## Context

Two features that look similar and are not: "do not disturb me at night" and
"I do not care about this conversation".

The first implementation treated both as suppression, and users lost messages.
Someone who had set quiet hours never found out about a direct message sent at
midnight, because it was discarded rather than held.

## Decision

Quiet hours **defer**. The notification is re-enqueued with a scheduled
delivery time at the end of the window in the user's local time, and arrives
in their morning.

Mute **drops**. It is discarded and never comes back, because the user said
they do not want it.

Urgency breaks quiet hours, since the point of quiet hours is "not unless it
matters". Urgency does not break mute, since the point of mute is "not at
all", and the flag is set by whoever sent the message rather than by the
person receiving it.

## Consequences

Good:

* Nobody loses a message to a preference they set about timing.
* The distinction is one boolean on the decision object and the tests state it
  explicitly, so it cannot be quietly collapsed back into one behaviour.
* Deferral rides on Service Bus scheduled delivery, so it survives a restart.
  Holding it in the function's memory would not.

Bad:

* A user waking at 07:00 gets everything at once. There is a digest for group
  chatter but direct messages arrive individually, and eleven of them at
  breakfast is its own kind of unpleasant.
* Changing your quiet hours does not reschedule anything already deferred,
  because it is already sitting in the queue with a time on it. Arguably
  wrong; nobody has complained.
* Scheduled messages count against the queue's message count, so the backlog
  metric shows an overnight hump that is entirely normal and looks alarming on
  a dashboard.
