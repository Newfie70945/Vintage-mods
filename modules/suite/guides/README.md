# Newfie's Server Essentials Suite — Guides

These guides ship with **Newfie's Server Essentials Suite 1.2.43** for **Vintage Story 1.22.7 / .NET 10**.

## Start here

| If you want to… | Read |
|---|---|
| Get a new server running quickly | [QUICKSTART.md](QUICKSTART.md) |
| Install/build the suite and configure features | [SETUP.md](SETUP.md) |
| Upgrade an existing server safely | [UPGRADING.md](UPGRADING.md) |
| Configure/tune PacketGuard and Client Integrity | [ANTICHEAT.md](ANTICHEAT.md) |
| Configure/export the public WebMap | [WEBMAP.md](WEBMAP.md) |
| Look up chat commands | [COMMANDS.md](COMMANDS.md) |
| Follow common staff workflows | [GUIDES.md](GUIDES.md) |
| Diagnose a problem | [TROUBLESHOOTING.md](TROUBLESHOOTING.md) |

## Current component versions

- **Server Essentials:** 1.2.43
- **Anti-Cheat config schema:** v62
- **AntiCheat Client companion:** 1.1.28
- **Player List companion:** 1.1.2

The main Server Essentials mod remains **`requiredOnClient: false`**. The AntiCheat Client and Player List are separate projects with their own version numbers.

## Important concepts

- **Shift+L** is the in-game staff GUI when the `ingamegui` feature is enabled and the player has the required privileges.
- **PacketGuard** is the server-authoritative movement/reach/mining/packet protection layer inside Server Essentials.
- **Client Integrity / AntiCheat Client** is the separate client-side companion used for client-mod/runtime integrity checks when you choose to require it.
- **WebPanel** is the browser administration interface.
- **WebMap** is a separate public-map runtime and export system. Enabling the WebMap feature gate does not automatically enable the public listener.

## Language support

The suite includes **English, German, French, and Russian** UI translations for the supported Server Essentials surfaces. Long Shift+L labels use guarded auto-fit logic so translations should not overlap or render inverted. Technical identifiers such as detector codes, registry IDs, mod IDs, hashes, commands, and raw evidence remain untranslated intentionally.


- **DISCORD.md** — unified Discord module setup, account linking, Shift+L controls, WebPanel API, and token security boundary.

- `AUTOMATED-RESTARTS.md` — UTC restart scheduling, warnings, `/restarttime`, Discord notices, and host relaunch setup


## UI1 interface previews

These screenshots use disposable browser fixtures; they show layout and styling, not live server metrics.

- [Role editor](webpanel-ui1-roles-desktop.png)
- [Player investigation](webpanel-ui1-player-investigation-desktop.png)
- [Profiler attribution](webpanel-ui1-profiler-attribution-desktop.png)
- [Role editor on mobile](webpanel-ui1-roles-mobile.png)

The UI preserves owner themes, accent settings, density and reduced motion. Player tabs do not fetch inventories automatically. Role field groups collapse locally without changing server configuration.

## UI2 previews

Disposable browser fixtures, not live server metrics.

- [Role Config and frame texture](webpanel-ui2-roles-desktop.png)
- [Chat layout editor](webpanel-ui2-chat-layout-desktop.png)
- [Profiler](webpanel-ui2-profiler-attribution-desktop.png)
- [Mobile roles](webpanel-ui2-roles-mobile.png)
