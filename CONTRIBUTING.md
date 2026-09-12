# Contributing — Yousof

## Start with the behaviour

Describe the user-visible problem, the smallest proposed change and the trade-off.
For a bug, include a reproduction and a regression test that fails before the fix.
Keep dependency upgrades separate from behaviour changes when possible.

## What matters in this repository

Mute/presence precedence, recipient-local quiet hours and deferred delivery. Use explicit timestamps and timezones in policy tests.

## Verification

```bash
dotnet test src/MessagePlatform.Notifications.Tests/MessagePlatform.Notifications.Tests.csproj
```

The CI gate covers the policy on .NET 8. It does not establish deployability of the historical Functions host, bindings, Bicep or omitted adapters.

## Before opening a change

- Run the relevant checks and record the command and result in the pull request.
- Update examples and the README if setup, public behaviour or limitations change.
- Explain any data migration, compatibility impact and rollback approach.
- Add a short architecture decision when a boundary or operational guarantee changes.
- Include only synthetic fixtures and example configuration, with no customer records or credentials.

Follow `.editorconfig` and keep unrelated formatting changes out of the diff.
Dependency-update pull requests require review and verification; nothing is auto-merged.
