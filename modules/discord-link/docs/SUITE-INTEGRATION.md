# Server Essentials integration contract

This module deliberately uses the Vintage Story server event bus so Newfie's Server Essentials Core, Client Integrity / Anti-Cheat, and future modules can publish events without referencing the Discord Integration DLL.

## Reload Discord config from the web panel

After the Server Essentials backend safely writes `ModConfig/NewfiesServerEssentialsDiscord.json`, publish:

```csharp
sapi.Event.PushEvent("newfiesse:discord-reload-config");
```

The module reloads the config and reconnects Discord when necessary.

## Anti-Cheat report event

Event name:

`newfiesse:anticheat-report`

Payload: `TreeAttribute`

Supported keys:
- `reportId` string
- `playerName` string
- `playerUid` string (not shown publicly by default)
- `detection` string
- `severity` string: Info, Low, Medium, High, Critical
- `confidence` float percentage
- `action` string
- `evidence` string
- `position` string

Example:

```csharp
var report = new TreeAttribute();
report.SetString("reportId", "AC-000421");
report.SetString("playerName", player.PlayerName);
report.SetString("playerUid", player.PlayerUID);
report.SetString("detection", "Movement anomaly");
report.SetString("severity", "High");
report.SetFloat("confidence", 92f);
report.SetString("action", "Flagged");
report.SetString("evidence", "Speed exceeded server-side movement envelope");
report.SetString("position", "124, 107, -332");
sapi.Event.PushEvent("newfiesse:anticheat-report", report);
```

## Authoritative admin audit event

Event name:

`newfiesse:admin-audit`

Payload keys:
- `actor` string
- `action` string
- `detail` string
- `result` string
- `source` string

Example:

```csharp
var audit = new TreeAttribute();
audit.SetString("actor", admin.PlayerName);
audit.SetString("action", "Ban player");
audit.SetString("detail", "/ban Example griefing");
audit.SetString("result", "Success");
audit.SetString("source", "Server Essentials Moderation");
sapi.Event.PushEvent("newfiesse:admin-audit", audit);
```

The Discord module also has a fallback observer for slash commands typed by elevated users. That observer records that a command was seen; it cannot reliably claim command success. Server Essentials Core should publish the authoritative audit event after its own actions complete.

## Channel inheritance

Every routed event channel uses `0` to mean "same as game chat". This lets joins, leaves, deaths, Anti-Cheat reports, and audit records share the game-chat channel or route independently.

## Discord staff command request contract

Discord Integration publishes `newfiesse:discord-staff-command` for moderation actions that Server Essentials Core owns.

Payload fields:

- `command`: `ban`, `unban`, `mute`, or `unmute`
- `player`: target player name
- `reason`: optional staff reason
- `discordUserId`: Discord snowflake as a string
- `discordUser`: display name used for audit output
- `discordInteractionId`: interaction snowflake for correlation

Core should process the event synchronously when possible and set these values back on the same `TreeAttribute`:

- `handled` (bool): Core recognized and processed the request
- `success` (bool): action succeeded
- `response` (string): safe staff-facing result message

Discord Integration performs its own Discord role check before emitting this event and writes the attempt/result to Admin Audit. Core MUST still enforce its own authorization/policy rules; the Discord role check is not a substitute for server-side authorization.
