# message-azure

Azure side of the MessagePlatform: Bicep infrastructure and the notification
function. Pairs with [dotnet-showcase](https://github.com/yght/dotnet-showcase),
which is the API, and [message-web](https://github.com/yght/message-web),
which is the client.

*Rebuild note. This is a cleaned-up version of work from 2021. Subscription
ids, tenant details and resource names from the original are gone; the
architecture, the RBAC model and the notification rules are the real ones. I'm
happy to walk through the live setup.*

## What I want to demonstrate

I want to show how I express customer-facing notification rules as testable code and connect them to Azure infrastructure.

- **Domain design:** explicit precedence for mute, presence, quiet hours and urgency.
- **Azure architecture:** Bicep modules, managed identity and service-specific access roles.
- **Asynchronous processing:** scheduled deferral and an idempotency-key contract for notification delivery.
- **Customer experience:** respecting notification preferences while keeping important messages visible.

**Start here:** [notification policy](src/MessagePlatform.Notifications.Core/NotificationPolicy.cs), [policy tests](src/MessagePlatform.Notifications.Tests/NotificationPolicyTests.cs), and [Bicep orchestration](infra/main.bicep).

**Scope:** the policy, infrastructure definitions and function outline are available for review. Concrete recipient-store and notification-sender implementations and their runtime registration are not included. Passing an idempotency key expresses a contract; enforcing deduplication belongs in the sender implementation. This is not a complete deployed notification service.

## The interesting problem

Deciding whether to interrupt somebody.

Every other part of this is plumbing. The hard bit is that a notification is
the only part of a messaging system that reaches a person who did not ask to
be reached at that moment, and getting it wrong in either direction is bad in
a different way. Notify too much and they turn push off, permanently, and then
they miss the one that mattered. Notify too little and the product doesn't
work.

`NotificationPolicy.Decide` is the whole decision, as a pure function. The
ordering of its rules is the design:

1. **Never notify you about your own message.** Obvious, and it happened —
   a group fan-out included the sender in the recipient list.
2. **A muted conversation wins over everything.** Including urgency. Somebody
   else setting an `urgent` flag is not permission to override what this user
   explicitly asked for.
3. **Presence beats everything except mute.** If they're looking at the app,
   the message is already on screen. A push for something visible is the
   fastest way to get push disabled.
4. **Quiet hours defer, they don't drop.** The distinction matters: dropped
   means never heard about, deferred means heard about at 7am.
5. **Urgency breaks quiet hours** but not mute, and not presence.
6. **Group chatter batches** past a threshold. Direct messages never batch,
   however many are pending — a one-to-one message is not chatter.

Quiet hours are wall-clock local, so the check converts into the recipient's
IANA zone rather than comparing UTC. A window that wraps midnight (22:00 to
07:00) is the normal case, and there's a test for the day-sleeper whose window
doesn't wrap. An unrecognised timezone string **fails open and sends** — the
alternative is silencing somebody indefinitely because of a bad value in their
profile.

27 tests, and they're worth reading before the implementation.

## Identity, and the absence of secrets

There is not one connection string or account key anywhere in this repository
or in the deployed app settings. Both data services have key auth turned off
outright:

- Service Bus namespace: `disableLocalAuth`
- Cosmos account: `disableLocalAuth: true`

The function has a system-assigned managed identity, and `main.bicep` grants
it Service Bus Data Receiver, Data Sender, and the Cosmos data-plane role.
That last one is a genuine trap worth knowing: **Cosmos data access is not an
ARM role assignment.** It's `Microsoft.DocumentDB/.../sqlRoleAssignments`,
a different resource type with its own role definitions. The ARM role called
"Cosmos DB Account Reader" grants control-plane access and reads exactly zero
documents.

The app settings use the `__fullyQualifiedNamespace` and `__accountEndpoint`
forms, which is what tells the bindings to authenticate as the identity rather
than go looking for a connection string.

## Layout

```
infra/
  main.bicep                 orchestration + the RBAC grants
  modules/servicebus.bicep   topic, subscription, deferred queue
  modules/cosmos.bicep       partitioning and indexing policy
  modules/functionapp.bicep  plan, storage, identity, settings
  modules/keyvault.bicep     RBAC-mode vault
  modules/monitoring.bicep   workspace-based App Insights
src/
  ...Core/                   the policy. Pure, netstandard2.0, no Azure types
  ...Functions/              Service Bus trigger. Fetch, decide, act
  ...Tests/                  27 xUnit tests against Core
pipelines/
  azure-pipelines.yml        build, test, dev, what-if, approval, prod
```

Two Cosmos choices worth defending. **Session consistency, not Strong** — a
user must read their own writes, but one user doesn't need to see another's
instantly, and Strong roughly doubles the RU cost and pins you to one region.
**Partitioned on `conversationId`** — every read this app makes is "messages
in one conversation, newest first", which is a single-partition query on that
key and a fan-out on any other. The cost is that one enormous conversation
becomes a hot partition; we never hit it, and the fix would be a key suffix.

The indexing policy excludes `/*` and includes three paths. The Cosmos default
indexes everything, and on a write-heavy container that is most of your RU
spend for no benefit, because nobody queries on message body.

## Running the checks

```bash
dotnet build src/MessagePlatform.Notifications.sln --warnaserror
dotnet test  src/MessagePlatform.Notifications.sln

az bicep build --file infra/main.bicep --stdout > /dev/null
```

All three pass clean. `--warnaserror` is in the pipeline too — a nullable
warning ignored for six months is a null reference in production.

Deploying needs an Azure subscription and the two service connections named in
the pipeline. Nothing here will deploy by accident.

## What I'd change

The policy takes `DateTime.UtcNow` from the function and threads it through as
a parameter, which is right, but `RecipientState` is fetched with no caching —
one Cosmos read per notification per recipient. At the volumes this ran at
that was fine and it's the first thing that would bite under load.

Deferred notifications go back onto a queue with a scheduled enqueue time.
That's durable and simple, but it means a user who changes their quiet hours
doesn't affect anything already scheduled. Correct behaviour is arguable; the
current behaviour was not really decided so much as inherited.

`enablePurgeProtection` on the vault is on, which is right for production and
irritating in dev, where a torn-down environment leaves a name reserved for 90
days. Dev should probably differ here and doesn't.

No integration test runs against real Azure. The policy is covered thoroughly
and the bindings are not covered at all, which is the usual shape — and the
usual place the bugs actually are.
