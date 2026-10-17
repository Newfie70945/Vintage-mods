# Unified Discord Integration

Server Essentials: **1.2.27+**  
Newfie's Server Essentials - Discord: **1.0.0+**  
Target: **Vintage Story 1.22.7 / .NET 10**

Starting with Discord 1.0.0, bot integration and account linking are one server-side module. The old standalone `NewfiesDiscordLink` package is no longer required and should not be installed beside 1.0.0+.

## Shift+L

Open **Shift+L → Integrations → Discord**.

Discord has four in-game pages:

- **Overview** — unified module/bot state, account-linking state, token-configured flag, chat bridge, Guild ID, invite URL, and main-channel test.
- **Announcements** — enable, channel ID, and message format per event.
- **Linking** — account-linking enable state, code length/expiry/cooldown, multiple-account policy, logging, `/link` command availability, and live counts.
- **Routing** — main chat, admin audit, Newfie's Client Integrity / Anti-Cheat destinations, tests, and connected guild text-channel discovery.

The Discord child tab is shown when the unified Discord module responds to the shared control event.

## WebPanel

The WebPanel has an **Administration → Discord** page with the same authoritative settings. It uses:

- `GET /api/discord`
- `POST /api/discord/settings`
- `POST /api/discord/test`

Read-only panel accounts can inspect the page but cannot change settings or send tests. Normal POST CSRF protection applies.

## Security boundary

The Discord bot token stays in `NewfiesServerEssentialsDiscord.json` on the server. It is never copied into the suite control event, never sent to Shift+L clients, and never returned by the WebPanel API. The UI only receives `botTokenConfigured=true/false`.

## Shared module contract

Server Essentials publishes synchronous settings/status requests on:

```text
newfiesse:discord-control
```

The unified Discord module owns transport, chat, announcements, routing, and `link.*` account-linking settings.

Other suite modules can still query or observe linked identity through:

```text
newfiesse:discord-link-query
newfiesse:discord-link-changed
```

This keeps Server Essentials free of a hard DLL reference to the Discord module while still presenting one unified Discord configuration surface.
