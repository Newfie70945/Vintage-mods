# Newfie's Server Essentials - Discord — Setup Guide

Target: **Vintage Story 1.22.7 / .NET 10**  
Unified Discord module: **1.0.0+**  
Server Essentials UI integration: **1.2.16+ recommended**

Starting with 1.0.0, Discord bot integration and Discord account linking are one server-side mod. Do not install the old standalone `NewfiesDiscordLink` mod beside this version.

---

## 1. Install the module

Place the built `NewfiesServerEssentialsDiscord_1.0.10.zip` in the Vintage Story server `Mods` folder.

Optional but recommended:

- Newfie's Server Essentials Suite 1.2.16+ for **Shift+L → Integrations → Discord** and the WebPanel Discord page.

Players do not need the Discord module installed on their clients.

Start the server once so the config is generated, then stop it before entering the bot token.

Primary config:

```text
ModConfig/NewfiesServerEssentialsDiscord.json
```

---

## 2. Upgrading from the old two-mod layout

If you previously used both `NewfiesServerEssentialsDiscord` and `NewfiesDiscordLink`:

1. Stop the server.
2. Remove the old `NewfiesDiscordLink_*.zip` from the Mods folder.
3. Install the unified Discord 1.0.0+ package.
4. Leave `ModConfig/NewfiesDiscordLink.json` in place for the first upgraded start if you want its link-policy settings imported.
5. Start the server.
6. Check the log for the legacy-config migration message.
7. After confirming settings, archive/delete the old `NewfiesDiscordLink.json` if desired.

Existing linked accounts do **not** need to be relinked. The unified module deliberately uses the same world-save database key:

```text
newfiesdiscordlink:database-v1
```

Pending one-time codes are memory-only and are expected to disappear after a restart.

---

## 3. Create the Discord application and bot

Use the Discord Developer Portal to create an application for your Vintage Story server and configure its bot user.

Keep the bot token private. It belongs only in the server-side `NewfiesServerEssentialsDiscord.json` file. Never put it in Discord chat, screenshots, Git repositories, public support posts, or the WebPanel.

### Message Content Intent

The normal Discord → Vintage Story chat bridge reads regular guild messages, so enable **Message Content Intent** in the application's Bot settings when using that feature.

Slash commands such as `/link`, `/players`, and `/serverinfo` do not themselves require message-content access. Discord treats Message Content as a privileged intent; larger/verified applications can have additional approval requirements. Use only the intents your deployment actually needs.

---

## 4. Install the app in your Discord server

Install/add the Discord application to the guild used by the Vintage Story server.

For the normal feature set, the bot needs access to the configured channels and permissions sufficient for:

```text
View Channels
Send Messages
Read Message History
Use Application Commands
```

Administrator permission is not required for the normal chat, announcement, and account-linking flow.

The module registers guild slash commands against the configured `GuildId`, so command changes appear quickly compared with global application-command propagation.

---

## 5. Copy IDs from Discord

Enable Discord Developer Mode, then copy the numeric IDs for:

- Server/Guild
- Main game-chat channel
- Optional announcement channels
- Optional admin-audit channel
- Optional Client Integrity / Anti-Cheat channel
- Optional Discord staff roles

Use numeric IDs, not channel or role names.

Example only:

```text
Guild:       123456789012345678
Main chat:   234567890123456789
Staff log:   345678901234567890
Anti-Cheat:  456789012345678901
```

---

## 6. Configure the unified Discord module

> **New-install default:** every user-facing Discord feature is OFF until you enable it. This includes the bot transport, chat bridge, event announcements, slash-command groups, account linking, admin audit, and Anti-Cheat routing. Configure IDs/token first, then opt in to the features you want. Existing config files are not reset on upgrade.

Edit:

```text
ModConfig/NewfiesServerEssentialsDiscord.json
```

Minimum useful settings:

```json
{
  "Enabled": true,
  "BotToken": "YOUR_REAL_BOT_TOKEN",
  "GuildId": 123456789012345678,
  "InviteUrl": "https://discord.gg/YOURINVITE",
  "Chat": {
    "Enabled": true,
    "ChannelId": 234567890123456789,
    "GameToDiscord": true,
    "DiscordToGame": true,
    "GameToDiscordFormat": "{player}: {message}",
    "DiscordToGameFormat": "{user}: {message}",
    "BoldGameSenderLabel": true,
    "UseGameRoleColor": false,
    "DiscordNameColor": "#5865F2",
    "ShowDiscordTag": false,
    "BoldDiscordSenderName": false
  },
  "Commands": {
    "Enabled": true,
    "EnablePlayerInfoCommands": true,
    "EnableStaffCommands": true,
    "EnableRestartCommands": true,
    "EnableLinkCommands": true,
    "AllowDiscordAdministrators": true,
    "StaffRoleIds": [],
    "RulesText": "Read the full rules in #rules."
  },
  "Linking": {
    "Enabled": true,
    "CodeLength": 8,
    "CodeExpirySeconds": 300,
    "CodeCooldownSeconds": 30,
    "MaxFailedAttemptsPerDiscordUser": 8,
    "FailedAttemptWindowSeconds": 300,
    "AllowOneDiscordAccountToLinkMultiplePlayers": false,
    "LogLinkChanges": true
  }
}
```

For Discord linking to work, all of these must be enabled/configured:

```text
Enabled
BotToken
GuildId
Commands.Enabled
Commands.EnableLinkCommands
Linking.Enabled
```

---

## 7. Account-linking policy

Recommended production defaults:

```text
CodeLength = 8
CodeExpirySeconds = 300
CodeCooldownSeconds = 30
MaxFailedAttemptsPerDiscordUser = 8
FailedAttemptWindowSeconds = 300
AllowOneDiscordAccountToLinkMultiplePlayers = false
LogLinkChanges = false
```

The module uses permanent Vintage Story **PlayerUID**, not character names, for stored identity.

Link codes:

- are generated with a cryptographically secure random generator;
- exclude ambiguous characters such as 0/O and 1/I/L;
- are indexed internally using SHA-256 hashes;
- remain only in memory;
- expire automatically;
- have per-player generation cooldowns;
- have per-Discord-user failed-code throttling.

By default, one Discord account may link to only one Vintage Story PlayerUID.

---

## 8. Verify the bot and UI

Start the server and check the server log. The unified module should report its linked-account count and, when enabled/configured, connect the Discord bot.

With Server Essentials 1.2.16+:

```text
Shift+L
→ Integrations
→ Discord
```

The Overview page should show:

```text
Discord module: loaded
Discord bot: connected/disconnected
Account linking: enabled/disabled
Bot token configured: yes/no
Linked accounts: <count>
```

The bot token value itself is never returned to the game client or WebPanel APIs.

The matching WebPanel page uses the same authoritative server config.

---

## 9. Test account linking

### In Vintage Story

Player runs:

```text
/discordlink
```

The game returns a one-time code.

Other in-game commands:

```text
/discordlink status
/discordlink cancel
/discordlink unlink confirm
```

### In Discord

The same user runs:

```text
/link <code>
```

On success the unified module stores:

```text
Vintage Story PlayerUID ↔ Discord user ID
```

Then test:

```text
/linkstatus
```

To remove the mapping from Discord:

```text
/unlink
```

Link-related Discord responses are ephemeral.

---

## 10. Admin account-link commands

Vintage Story users with `controlserver` can use:

```text
/discordlinkadmin stats
/discordlinkadmin status <player>
/discordlinkadmin discord <discordUserId>
/discordlinkadmin unlink <player> confirm
```

---

## 11. Discord slash commands

Depending on configuration, the unified module can register:

Player/info:

```text
/players
/restarttime
/serverinfo
/date
/discord
/rules
/help
```

Account linking:

```text
/link
/linkstatus
/unlink
```

`/restarttime` reads Server Suite's live automated-restart scheduler over the suite event bus. If the scheduler is unavailable, the bot reports that instead of inventing a time.

Staff:

```text
/announce
/kick
/allowcharsel
/whitelist
/ban
/unban
/mute
/unmute
```

`/whitelist` uses Vintage Story's native `/player <name> whitelist on|off` command, so the game remains authoritative for whitelist state. Ban/mute state remains authoritative in Server Essentials Core when those suite moderation commands are used.

---

## 12. Announcements and routing

In Shift+L or WebPanel, configure:

```text
Player Join
Player Leave
Player Death
Server Started
Server Stopping
Discord Linked
Discord Unlinked
Admin Audit
Client Integrity / Anti-Cheat
```

Announcement routes with channel ID `0` inherit the main configured chat channel where supported.

When Server Suite automated restarts are enabled, restart countdown notices are sent through the configured **Server Stopping** channel. If that route has channel ID `0`, the main Discord chat channel is used as the fallback. The restart scheduler's own **Discord announcements** toggle controls whether those countdown notices are emitted.

The same settings are stored server-side regardless of whether they were changed through Shift+L, WebPanel, or the JSON config.

---

## 13. Security notes

- Never expose the bot token to players or browser clients.
- Use a dedicated Discord bot token for the server.
- Give the bot only the Discord permissions it actually needs.
- Keep `AllowOneDiscordAccountToLinkMultiplePlayers` false unless you intentionally support account sharing/alts.
- Do not post one-time link codes publicly.
- Restrict WebPanel administrator accounts appropriately.
- Back up the Vintage Story world/save data; persistent account mappings live with the world save.
- Link/unlink announcements are configurable and can be disabled if you do not want account identity changes announced publicly.

---

## 14. Troubleshooting

### Discord tab says the module is not installed

Verify the unified `NewfiesServerEssentialsDiscord` package is in the server Mods folder and loaded without an exception. Server Essentials talks to it through `newfiesse:discord-control`.

### Bot token configured but bot is disconnected

Check:

```text
Enabled = true
BotToken is current
GuildId is correct
The bot/app is installed in that guild
Required gateway intent is enabled for the chat bridge
Server logs for Discord.Net errors
```

### `/link` does not appear

Check:

```text
Commands.Enabled = true
Commands.EnableLinkCommands = true
GuildId is correct
Bot is connected
```

Changing command registration settings can require the Discord connection to restart so the guild command list is rewritten.

### `/link` says linking is disabled

Check:

```text
Linking.Enabled = true
```

### `/discordlink` exists twice or command registration fails

Remove the old standalone `NewfiesDiscordLink` mod. Unified Discord 1.0.0 owns `/discordlink` itself.

### Existing links disappeared after upgrade

Do not create a new world/save. The unified module uses the old save-data key `newfiesdiscordlink:database-v1`. Check the server log for database-load errors and restore the world save from backup if the save data itself was replaced.

---

## 15. First-run checklist

```text
[ ] Old standalone NewfiesDiscordLink mod removed
[ ] Unified Discord 1.0.0+ installed
[ ] Bot application created
[ ] Bot token entered server-side only
[ ] Guild ID configured
[ ] Main channel ID configured
[ ] Message Content Intent enabled if Discord → game chat is used
[ ] Commands.Enabled enabled
[ ] Commands.EnableLinkCommands enabled
[ ] Linking.Enabled enabled
[ ] Bot connected
[ ] /discordlink generates a code
[ ] Discord /link completes the link
[ ] /linkstatus reports the VS account
[ ] Shift+L Discord page reports account-link status
[ ] WebPanel Discord page reports the same settings
[ ] Old link settings migrated if upgrading
```

## Gateway intents and Message Content

NSE Discord only requests Discord's privileged **Message Content Intent** when Discord-to-game chat is enabled (`Chat.Enabled=true` and `Chat.DiscordToGame=true`). Normal announcements, slash commands, account linking, `/players`, and `/restarttime` do not require that privileged intent.

If you enable Discord-to-game chat, open the Discord Developer Portal, select your application, open **Bot**, and enable **Message Content Intent** under **Privileged Gateway Intents**. After changing this setting, reconnect/reload NSE Discord.

If Discord rejects the connection, NSE logs the gateway close code and a targeted hint. Code `4014` means Discord rejected a privileged intent that is not enabled/approved for the application.


### Game → Discord sender styling

- `BoldGameSenderLabel`: when enabled, the full Vintage Story sender label is bolded in Discord, including a visible role prefix such as `[Founder] Newfie`. Default: `true`.
- `UseGameRoleColor`: when enabled, game chat is sent as a Discord embed using the player's Vintage Story role RGB value as the embed accent color. Default: `false`.
- Discord does not support arbitrary inline text colors in ordinary messages, so the role color is represented by the embed accent rather than colored `[Role]` text.


## Config location

The unified Discord config is stored at `VintagestoryData/ModConfig/NewfiesServerEssentialsSuite/NewfiesServerEssentialsDiscord.json`. Existing root-level Discord config is relocated automatically on startup.

## Optional linked-account role synchronization (1.0.9)

Discord Integration can map selected Discord roles to Vintage Story privilege roles for linked accounts. The feature is disabled by default. Configure it under `Linking.RoleSync` in `NewfiesServerEssentialsDiscord.json`.

Role synchronization does not require the privileged Guild Members gateway intent. The bot reads each linked online member through Discord REST.

See `ROLE-SYNC.md` for the full safety model and examples. In particular, manually assigned Vintage Story roles take precedence: if an administrator changes a Discord-managed player to a different in-game role, automatic role sync suspends for that player instead of changing the role back.
