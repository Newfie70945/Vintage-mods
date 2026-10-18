# Newfie's Server Essentials - Discord 1.0.13

Current fixes: [current release notes](../../CHANGELOG.md).
Target: **Vintage Story 1.22.7 / .NET 10**

This is the single Discord module for Newfie's Server Suite. Starting with 1.0.0, the former standalone **Newfie's Discord Link** module has been merged into Discord Integration.

## One module now owns all Discord features

- Discord bot connection and guild slash commands
- `/discord` invite command
- `/discordstatus` and `/discordreload` admin commands
- Game ↔ Discord global chat bridge
- Join, leave, death, server start/stop announcements
- Per-event channel routing and message formats
- Admin-audit routing
- Newfie's Client Integrity / Anti-Cheat routing
- `/discordlink` and `/discordlinkadmin` in-game commands
- Discord `/link`, `/linkstatus`, and `/unlink`
- Staff `/whitelist` command with Add/Remove actions backed by Vintage Story's native whitelist
- Persistent Vintage Story PlayerUID ↔ Discord user-ID mappings
- Optional Discord-role → Vintage Story role synchronization for linked accounts
- Manual-role protection: Discord only removes/changes roles it previously assigned itself
- One-time code generation, expiry, cooldown, and brute-force throttling
- Shift+L → Integrations → Discord controls through Server Essentials
- WebPanel → Discord controls through Server Essentials
- Automated restart countdown notices from Server Suite 1.2.16, routed through the configured Server Stopping channel (or chat-channel fallback)

The Discord bot token remains server-side and is never returned through Shift+L, WebPanel status APIs, or client packets.

## Discord role sync (1.0.9)

Linked accounts can optionally receive a Vintage Story role from selected Discord roles. The feature is disabled by default and only mappings listed under `Linking.RoleSync.Mappings` participate. If a member has multiple mapped Discord roles, the highest-priority mapping wins.

Role Sync is deliberately conservative: it only takes initial ownership from configured safe base roles (default `suplayer`). It remembers the player's previous role and restores it when the mapped Discord role disappears **only if the player is still on the role Discord assigned**. If an administrator manually changes the player's in-game role, automatic role sync detects that change, suspends itself for that player, and never changes the manual role back.

See `docs/ROLE-SYNC.md` for configuration, priorities, manual-override behavior, and `/discordrolesync` admin commands. Role lookups use Discord REST and do not require the privileged Guild Members gateway intent.

## Opt-in defaults

New installations are intentionally inert. The Discord transport, chat bridge, announcements, slash-command groups, account linking, admin audit, and Anti-Cheat routing all default to **OFF**. Configure the bot token/Guild/channel values first, then enable only the features you want from Shift+L, WebPanel, or the config file. Existing saved configurations are preserved on upgrade; updating the mod does not forcibly turn configured features off.

Player-info slash commands include `/players` and `/restarttime`. `/restarttime` reads the live automated-restart schedule from Server Suite 1.2.16 when that scheduler is available.

## Upgrade from the old two-mod layout

1. Stop the Vintage Story server.
2. Remove the old standalone `NewfiesDiscordLink_*.zip` from the server Mods folder.
3. Install `NewfiesServerEssentialsDiscord_1.0.10.zip` or newer.
4. Keep your existing world/save files. Link records use the same save-data key as the old module and load automatically.
5. Keep `ModConfig/NewfiesDiscordLink.json` for the first upgraded start if you want its settings migrated. The unified module imports that file once into `NewfiesServerEssentialsDiscord.json` and marks migration complete.
6. After confirming the unified settings, the old `NewfiesDiscordLink.json` can be archived or deleted.

Do **not** run the old standalone Discord Link mod beside 1.0.0; both would attempt to own `/discordlink` and the legacy link events.

## Build prerequisites

Vintage Story 1.22.x uses .NET 10. Install the .NET 10 SDK and set `VINTAGE_STORY` to the Vintage Story installation directory containing `VintagestoryAPI.dll`.

```text
dotnet restore
dotnet build -c Release
```

`Discord.Net 3.20.1` is referenced through NuGet.

For installation, Discord Developer Portal setup, permissions, configuration, account-linking policy, Shift+L/WebPanel controls, testing, migration, and troubleshooting, see:

`docs/SETUP-GUIDE.md`

## Runtime dependency packaging

The final Vintage Story mod ZIP must contain the Discord.Net runtime DLLs beside `NewfiesServerEssentialsDiscord.dll`. Do **not** package only the main mod DLL. The included `build-release.ps1` / `build-release.sh` scripts verify and package `Discord.Net.Core.dll`, `Discord.Net.Rest.dll`, `Discord.Net.WebSocket.dll`, `Discord.Net.Dave.dll`, and `Newtonsoft.Json.dll`. The project references only `Discord.Net.WebSocket` rather than the full Discord.Net metapackage, which keeps the shipped dependency set smaller.


### Chat relay compatibility

Game-to-Discord chat strips Vintage Story's built-in `<strong>Player:</strong>` VTML sender prefix before applying the Discord format, preventing duplicated names and literal VTML in Discord. If Server Suite **Chat Display → Show roles before names** is enabled, the Discord relay mirrors the Vintage Story role tag before the player name.

Join/leave formats support `{player}`, `{online}`, and `{max}` placeholders. The stock formats are:

- `📥 ***Player {player} connected ({online}/{max})***`
- `📤 ***Player {player} disconnected ({online}/{max})***`


### Discord -> game sender styling
The WebPanel can set one color for all Discord sender names relayed into Vintage Story chat. Only the sender label is colored; message content stays at the normal chat color. An optional `[DC]` prefix can be enabled. The defaults are `#5865F2` and prefix OFF.


## Configuration location

Discord 1.0.9 stores `NewfiesServerEssentialsDiscord.json` under `VintagestoryData/ModConfig/NewfiesServerEssentialsSuite/`. Existing root-level unified Discord and legacy Discord Link configs are relocated automatically and archived before removal.


## Community MOTD integration (1.0.9)
Configure `Motd.AnnouncementsChannelId`, `Motd.UpdatesChannelId`, and `Motd.EventsChannelId` to publish recent Discord channel messages into the matching Newfie's Community MOTD tabs. `Motd.MaxItemsPerFeed` controls the number of recent messages (1-25).


## Shift+L Community MOTD tab
When Server Suite 1.2.34 and Community MOTD 2.1.1 are installed, the Suite-hosted Shift+L -> Integrations -> Discord page exposes an optional Community MOTD configuration tab. The Discord module does not own the MOTD config; the Suite talks to Community MOTD through its runtime companion bridge, preserving independent builds and optional installation.


### Manual role ownership
If an administrator wants to keep a role that Discord previously assigned but make it permanently manual, use `/discordrolesync release <player>`. The current role is left unchanged and Discord ownership is discarded.
