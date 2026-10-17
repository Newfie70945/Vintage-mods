# Setup Guide

Newfie's Server Essentials Suite **1.2.35**
Target: **Vintage Story 1.22.7 / .NET 10**

For the shortest path to a working server, see [QUICKSTART.md](QUICKSTART.md). This document explains the full setup.

## 1. Build and install

Set `VINTAGE_STORY` to the Vintage Story installation used by the production server, then build:

```text
set VINTAGE_STORY=C:\Path\To\VintageStory
dotnet build NewfiesServerEssentialsSuite.csproj -c Release
```

The project references the server's Vintage Story assemblies directly, including `cairo-sharp` for the Shift+L custom UI. Use the same game installation/API files that the real server runs.

Place the built Server Essentials mod in the server `Mods` directory. Do not install the outer three-project source bundle as a single mod.

## 2. Understand the three projects

```text
ServerEssentials  1.2.35  server suite; required on server, optional on clients
AntiCheatClient   1.1.26  optional/required-by-policy client-integrity companion
PlayerList        1.1.1   optional public hold-Tab roster companion
```

The main suite remains `requiredOnClient: false`. Normal players do not need Server Essentials installed client-side, but staff who want the **Shift+L** administration GUI should run the matching Server Essentials build on their client.

## 3. First boot and configs

Start the server once. The Suite and Discord module create/configure their JSON under `VintagestoryData/ModConfig/NewfiesServerEssentialsSuite/`. Existing root-level Newfie configs are relocated there automatically on upgrade.

Common files inside `NewfiesServerEssentialsSuite/` include:

```text
NewfiesServerEssentialsFeatures.json
NewfiesServerEssentialsAdmin.json
NewfiesBlocklogger.json
NewfiesAnimalLimiter.json
NewfiesLoginProtection.json
NewfiesAntiCheat.json
NewfiesAnnouncements.json
NewfiesWebServer.json
NewfiesServerEssentialsDiscord.json
NewfiesWebMap.json
NewfiesPerformanceCenter.json
NewfiesOperationsCenter.json
NewfiesSupporterNicknames.json
```

Before large changes, back up `ModConfig` and the world save.

## 4. Feature gates

List current feature gates:

```text
/nsefeatures list
```

Current feature names include:

```text
blocklogger
ingamegui
admin
anticheat
animallimiter
loginprotection
webdashboard
webconsole
webmap
announcements
reports
analytics
diagnostics
modupdates
logintelligence
performancecenter
operationscenter
```

Change one:

```text
/nsefeatures <feature> on
/nsefeatures <feature> off
```

Or all optional gates:

```text
/nsefeatures all on
/nsefeatures all off
```

The setting is saved immediately, but **restart the server** after changing feature gates so listeners, timers, workers, and network hooks are rebuilt cleanly.

## 5. Staff privileges

The administration subsystem uses granular privileges including:

```text
nse.player.view
nse.player.manage
nse.moderation.warn
nse.moderation.mute
nse.moderation.ban
nse.reports.manage
nse.inventory.view
nse.inventory.edit
nse.teleport
nse.cleanup
nse.diagnostics
nse.server
blocklogger
```

Grant moderators only the capabilities they need. Server root/control users are accepted by the suite's administrative permission checks where appropriate.

Supporter perks use separate opt-in privileges and do not require giving players administrative permissions:

```text
supporterlamp
supporternickname
```

`supporternickname` enables `/nick` and `/nickname`; the default nickname cooldown is 24 hours and is configurable in `NewfiesSupporterNicknames.json`.

## 6. Shift+L staff GUI

With `ingamegui` enabled, authorized staff can open:

```text
Shift+L
```

Major workspaces include player administration, BlockLogger, Anti-Cheat, Packet Protection, Profiler/diagnostics, Animal Limiter, WebPanel, and WebMap.

The UI supports EN/DE/FR/RU. Long translated button text uses guarded auto-fitting/wrapping.

## 7. BlockLogger

Config:

```text
NewfiesBlocklogger.json
```

Verify:

```text
/blocklogger status
/blocklogger path
/blocklogger count
```

BlockLogger uses SQLite/WAL. Keep the active database on reliable local storage when possible.

## 8. Login Protection

Config:

```text
NewfiesLoginProtection.json
```

Check:

```text
/loginprot status
```

Typical enablement:

```text
/loginprot on
/loginprot 15
```

After changing it, test a real reconnect/respawn in a location where temporary protection is easy to verify.

## 9. Animal Limiter

Config:

```text
NewfiesAnimalLimiter.json
```

Inspect before culling:

```text
/animallimit status
/animallimit list
/animallimit preview
```

Recommended workflow:

```text
add one targeted rule
→ preview it
→ verify the matched population
→ cull only after the preview is correct
```

Example:

```text
/animallimit set spheniscidae:* 20 3
/animallimit preview spheniscidae:*
```

Avoid broad universal limiting on a heavily modded server until the rules have been verified against your entity set.

## 10. Anti-Cheat / Client Integrity

Config:

```text
NewfiesAntiCheat.json
```

Current Anti-Cheat config schema:

```text
v47
```

Basic checks:

```text
/anticheat status
/anticheat status <player>
/anticheat rescan <player>
```

For live PacketGuard configuration open:

```text
Shift+L → Anti-Cheat → Packet Protection
```

That page includes the key warning, noclip/collision, movement hardening, diagnostic-context, and suppression-telemetry controls. The WebPanel exposes the larger Anti-Cheat rule/risk configuration.

Read [ANTICHEAT.md](ANTICHEAT.md) before loosening movement/reach thresholds to solve a mod compatibility issue.

## 11. WebPanel

The browser administration service is configured by `NewfiesWebServer.json` and `/serverutils web ...`.

Check it:

```text
/serverutils web status
```

Safe local starting point:

```text
/serverutils web bind 127.0.0.1
/serverutils web password <strong-password>
/serverutils web port 9465
/serverutils web on
```

For public access, prefer a reverse proxy:

```text
/serverutils web publicurl https://admin.example.com
/serverutils web securecookies on
```

Do not expose an unauthenticated admin listener to the Internet.

## 12. WebMap

WebMap has separate controls:

1. the `webmap` feature gate;
2. the actual WebMap runtime `Enabled` setting.

A fresh/current install can have the feature available while the public map runtime remains off until you explicitly enable it.

Config:

```text
NewfiesWebMap.json
```

Player privacy commands:

```text
/webmap live status
/webmap live off
/webmap live on
```

Terrain export styles can be enabled independently (Normal, Sepia/parchment, Topographic). Large terrain exports should be scheduled deliberately. See [WEBMAP.md](WEBMAP.md).

## 13. Reports and moderation

Player report route:

```text
/report <issue>
```

Suite route:

```text
/nse report <issue>
```

Staff report tools live under `/nse reports...` and Shift+L Player Administration.

## 14. Character selection reset

For players who need to reopen character/class selection, Server Essentials Player Management uses the Vintage Story `allowcharselonce` mechanism. Online players are updated directly/synchronized; offline known players use the server-console player command path.

The player then runs:

```text
.charsel
```

## 15. Command conflicts

The suite does not blindly replace command roots already owned by another mod.

Notable examples:

- Th3Essentials may own `/restart`, `/shutdown`, and `/back`.
- Stratum may own `/restart`.
- another provider may own `/report`, `/webmap`, or `/anticheat`.

The stable suite namespace remains `/nse` where applicable. If a root is missing, check startup logs before assuming the subsystem failed.

## 16. Production checklist

```text
1. Start once and confirm no startup exception.
2. /nsefeatures list
3. /blocklogger status
4. /loginprot status
5. /animallimit status
6. /anticheat status
7. /serverutils web status (if used)
8. Open Shift+L and verify staff permissions.
9. Verify Packet Protection saved values.
10. Record a baseline Performance/Memory snapshot.
11. Back up world + ModConfig + persistent suite data.
```

## 17. Where to go next

- [QUICKSTART.md](QUICKSTART.md) — short checklist
- [COMMANDS.md](COMMANDS.md) — command reference
- [GUIDES.md](GUIDES.md) — staff workflows
- [ANTICHEAT.md](ANTICHEAT.md) — PacketGuard/Client Integrity
- [WEBMAP.md](WEBMAP.md) — public map/export
- [UPGRADING.md](UPGRADING.md) — safe upgrades
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common faults
