# 1. Managed identity only, no connection strings

Date: 2021-05-18
Status: Accepted

## Context

The standard Functions setup puts a Service Bus connection string and a Cosmos
key in app settings. Both are readable by anybody with Contributor on the
resource group, they end up in local `local.settings.json` files, and they get
pasted into support tickets.

We had a Cosmos key in a `.env` file in a repository once. That is the whole
argument.

## Decision

`disableLocalAuth` on the Service Bus namespace and Cosmos account, so keys do
not work at all, and a system-assigned managed identity on the function app
with explicit role grants in `main.bicep`.

Bindings use the identity-based configuration form —
`ServiceBusConnection__fullyQualifiedNamespace` rather than
`ServiceBusConnection` — which is what makes the extension use
`DefaultAzureCredential` instead of looking for a connection string.

## Consequences

Good:

* There is no credential to leak, because there is no credential. A key
  pasted into a ticket would not work.
* Access is visible in the portal as role assignments rather than implied by
  possession of a string.
* Revoking is removing a role assignment, which is instant, rather than
  rotating a key and chasing every consumer.

Bad:

* Local development is harder. `DefaultAzureCredential` falls back to the
  developer's `az login`, so every developer needs their own role assignments
  on the dev resources. That is more setup and it confuses new joiners.
* Role assignments are eventually consistent. A fresh deployment can fail its
  first few invocations while the grant propagates, which looks exactly like a
  permissions bug and is not one.
* The Cosmos data-plane role is a separate resource type and cannot be
  assigned through the portal UI at all, which is genuinely surprising the
  first time.
