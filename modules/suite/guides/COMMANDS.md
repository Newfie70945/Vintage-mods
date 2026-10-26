# Complete Command Reference

Newfie's Server Essentials Suite **1.2.35**

This reference is based on the command registrations and handlers in the current source tree.

## Command ownership and conflicts

The suite does not blindly replace commands already registered by another mod.

Conditional roots:

```text
/report
/webmap
/anticheat
/restart
/shutdown
/back
/spawn
/msg
/r
/starterkit
/setstarterkit
/resetstarterkitusage
/resetstarterkitusageall
```

When Th3Essentials is detected, root `/restart`, `/shutdown`, `/back`, `/spawn`, `/msg`, `/r`, `/starterkit`, `/setstarterkit`, `/resetstarterkitusage`, and `/resetstarterkitusageall` are left to Th3Essentials. When Stratum is detected, root `/restart` is left to Stratum. If any other provider already owns `/spawn`, `/msg`, `/r`, or another conditional root before NSE registers, NSE leaves that command untouched. Suite fallbacks remain available under `/nse` while the Administration feature is enabled.

---

## `/nsefeatures` — feature gates

Privilege: `controlserver`

```text
/nsefeatures list
/nsefeatures status
/nsefeatures help
/nsefeatures <feature> on|off
/nsefeatures <feature> true|false
/nsefeatures <feature> 1|0
/nsefeatures all on|off
```

Supported feature names:

```text
blocklogger
ingamegui
admin
playercommands
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

Changes require a server restart to fully add/remove the subsystem runtime hooks. The `webmap` master feature gate and the actual public WebMap runtime are separate; the module can be available while the public runtime remains OFF until explicitly enabled in `NewfiesWebMap.json`, Shift+L, or the dashboard.

---


## Player commands

These commands are available when the `playercommands` feature is enabled. Settings are stored in `ModConfig/NewfiesPlayerCommands.json`. The starter-kit snapshot itself is stored in `ModConfig/NewfiesStarterKit.json`; one-time claim history is stored in the world save by permanent PlayerUID.

```text
/spawn
/msg <player> <message>
/r <message>
/starterkit
/warp <name>
/warps
/warpinfo <name>
```

Administration commands:

```text
/setstarterkit
/resetstarterkitusage <online player>
/resetstarterkitusageall
/setwarp <name> [itemcode] [quantity]
/delwarp <name>
/setwarpcost <warp> <itemcode|none> [quantity]
/setwarpcooldown <warp> <seconds|default>
/setwarpwarmup <warp> <seconds|default>
/setwarpmove <warp> <on|off|default>
/warpreload
```

- `/spawn` teleports to Vintage Story's global `World.DefaultSpawnPosition`; it does **not** use a player's bed/custom spawn point. Default cooldown: 60 seconds.
- `/msg` sends a private in-game message to one online player.
- `/r` replies to the last player you exchanged a private message with.
- `/starterkit` grants the configured NSE starter kit once per permanent PlayerUID when NSE is the effective kit provider.
- `/setstarterkit` snapshots the admin's **occupied hotbar slots**, including full serialized ItemStack data/attributes. It does not reset existing claim history.
- Claims are atomic: configured target hotbar slots must be empty before the kit is applied. If the grant fails, the claim is not recorded.
- `/resetstarterkitusage <player>` clears one online player's NSE claim. `/resetstarterkitusageall` clears all NSE claim records.
- `/warp <name>` teleports to a configured destination. Each warp can independently override the global cooldown, warm-up, movement cancellation, optional item cost, and access policy (server roles and/or privileges).
- `/warps` lists only enabled destinations the player is allowed to use and shows their configured costs. `/warpinfo <name>` respects the same access policy and shows effective cooldown, warm-up, movement rule, position, item cost, and access summary. Root/controlserver staff bypass per-warp access rules for administration/testing.
- Warp definitions are stored in `ModConfig/NewfiesServerEssentialsSuite/NewfiesWarps.json`. Access is rechecked when warm-up completes so a role/privilege change cannot be bypassed. Item costs are rechecked immediately before teleport, consumed only after validation, and refunded if the teleport throws an error.
- `/nse warp`, `/nse warps`, `/nse warpinfo` and the `/nse` warp-administration fallbacks remain available when another mod already owns a root warp alias.
- When Server Essentials Administration is active, suite mutes also block `/msg` and `/r` by default.

### Starter Kit provider

`StarterKitProvider` controls only the starter-kit implementation:

- `Auto` — use Th3Essentials when it is installed; otherwise use NSE.
- `NSE` — use NSE's starter-kit implementation. If Th3Essentials is installed, Th3 still keeps the root `/starterkit` aliases and NSE remains available through `/nse starterkit`, `/nse setstarterkit`, and the `/nse` reset commands.
- `Th3Essentials` — delegate starter-kit behavior to Th3Essentials.

NSE never replaces Th3Essentials' root kit commands merely because `StarterKitProvider=NSE`. This avoids registration collisions and lets an admin intentionally keep both mods installed. Discord account linking is not part of this selector.

Default player-command configuration:

```json
{
  "SchemaVersion": 1,
  "SpawnEnabled": true,
  "SpawnCooldownSeconds": 60,
  "PrivateMessagesEnabled": true,
  "ReplyEnabled": true,
  "RespectSuiteMutes": true,
  "MaxPrivateMessageLength": 500,
  "StarterKitEnabled": true,
  "StarterKitProvider": "Auto",
  "WarpsEnabled": true,
  "WarpDefaultCooldownSeconds": 60,
  "WarpDefaultWarmupSeconds": 5,
  "WarpDefaultCancelOnMove": true
}
```

---

## Supporter nickname

Privilege: `supporternickname`

```text
/nick <nickname>
/nickname <nickname>
/nick reset
/nickname reset
```

- Nicknames are **presentation-only**. The player's real Vintage Story account name remains authoritative for permissions, moderation, bans, BlockLogger, admin tools, and other mods.
- Fresh installs use a **24-hour cooldown** (`CooldownMinutes: 1440`) between nickname changes/resets. Set it to `0` to disable the cooldown.
- Default nickname length is **3–20 characters**. Allowed characters are letters, numbers, spaces, apostrophes, underscores, and hyphens.
- Nicknames cannot impersonate another real account name already known to the server, cannot duplicate another active supporter nickname, and cannot use reserved staff/system names.
- When Suite Chat Display is active, the nickname is shown in chat. The Suite-backed Newfie's Player List roster also uses the nickname.
- Settings/state are stored in `ModConfig/NewfiesServerEssentialsSuite/NewfiesSupporterNicknames.json`.

Example supporter grant:

```text
/player <name> privilege grant supporternickname
```

---

## `/nse` — main administration tree

`/nse` is the stable suite administration root. Most operations require the listed suite privilege; server root/control users are also accepted by the permission checks.

| Command | Privilege | Purpose |
|---|---|---|
| `/nse spawn` | normal player | NSE fallback for global world spawn teleport. |
| `/nse msg <player> <message>` | normal player | NSE fallback for private messaging. |
| `/nse r <message>` | normal player | NSE fallback reply command. |
| `/nse starterkit` | normal player | Claim the NSE starter kit when NSE is the effective kit provider. |
| `/nse setstarterkit` | `controlserver` | Snapshot the admin hotbar as the NSE starter kit. |
| `/nse resetstarterkitusage <player>` | `controlserver` | Clear one online player's NSE starter-kit claim. |
| `/nse resetstarterkitusageall` | `controlserver` | Clear all NSE starter-kit claim records. |
| `/nse player [player]` | `nse.player.view` | Show tracked player status/profile. Defaults to yourself. |
| `/nse report <issue>` | normal report route | Submit a player report when Reports is enabled. |
| `/nse reports [open\|investigating\|resolved\|all]` | `nse.reports.manage` | List up to the current report view limit. |
| `/nse reportstatus <id> <open\|investigating\|resolved> [note]` | `nse.reports.manage` | Change report state. |
| `/nse reportassign <id> <me\|staff name\|clear>` | `nse.reports.manage` | Assign/unassign a report. |
| `/nse reportnote <id> <private staff note>` | `nse.reports.manage` | Add private report notes. |
| `/nse warn <player> <reason>` | `nse.moderation.warn` | Add a warning. |
| `/nse warnings <player>` | `nse.moderation.warn` | List warnings. |
| `/nse unwarn <player> <warningId>` | `nse.moderation.warn` | Remove a warning. |
| `/nse mute <player> <duration\|permanent> <reason>` | `nse.moderation.mute` | Mute player chat. |
| `/nse unmute <player>` | `nse.moderation.mute` | Remove suite mute. |
| `/nse muteinfo <player>` | `nse.moderation.mute` | Show mute state. |
| `/nse tempban <player> <duration> <reason>` | `nse.moderation.ban` | Temporary suite ban. |
| `/nse baninfo <player>` | `nse.moderation.ban` | Show temporary-ban state. |
| `/nse unban <player>` | `nse.moderation.ban` | Remove a suite temporary ban. |
| `/nse note <player> add <text>` | `nse.player.manage` | Add a private staff note. |
| `/nse note <player> list` | `nse.player.manage` | List private staff notes. |
| `/nse note <player> remove <id>` | `nse.player.manage` | Remove a private staff note. |
| `/nse history [player]` | `nse.player.view` | Combined recent admin + BlockLogger history. Defaults to yourself. |
| `/nse freeze <player> [duration\|permanent] [reason]` | `nse.player.manage` | Freeze a player. Defaults to permanent when duration is omitted. |
| `/nse unfreeze <player>` | `nse.player.manage` | Remove freeze. |
| `/nse goto <player>` | `nse.teleport` | Teleport yourself to player. |
| `/nse bring <player>` | `nse.teleport` | Bring player to you. |
| `/nse tphere <player>` | `nse.teleport` | Alias for bring. |
| `/nse back` | `nse.teleport` | Return to your previous suite teleport location. |
| `/nse sendback <player>` | `nse.teleport` | Return a player previously brought by the suite. |
| `/nse invsee <player>` | `nse.inventory.view` | Read-only inventory summary. |
| `/nse invsnapshot <player> [reason]` | `nse.inventory.edit` | Store a player inventory snapshot. |
| `/nse invrestore <player> <snapshotId> --confirm` | `nse.inventory.edit` | Restore a stored inventory snapshot. |
| `/nse invgive <player> <code> <qty>` | `nse.inventory.edit` | Give an item/block with automatic snapshot protection. |
| `/nse invqty <player> <inventoryId> <slot> <qty>` | `nse.inventory.edit` | Change one stack quantity. |
| `/nse invmove <player> <fromInv> <fromSlot> <toInv> <toSlot>` | `nse.inventory.edit` | Move/swap inventory slots. |
| `/nse invreplace <player> <inventoryId> <slot> <code> <qty>` | `nse.inventory.edit` | Replace one inventory slot. |
| `/nse invremove <player> <inventoryId> <slot> --confirm` | `nse.inventory.edit` | Remove one occupied slot with audit/undo. |
| `/nse heal <player>` | `nse.player.manage` | Heal player. |
| `/nse feed <player>` | `nse.player.manage` | Feed player. |
| `/nse revive <player>` | `nse.player.manage` | Revive player. |
| `/nse seen <player>` | `nse.player.view` | Show online/last-seen state. |
| `/nse playtime <player>` | `nse.player.view` | Show tracked total/session playtime. |
| `/nse afk` | `afk` | Toggle your integrated AFK mode when root `/afk` is unavailable. |
| `/nse afkinfo <player>` | `nse.player.view` | Show explicit `/afk` state plus approximate idle time. |
| `/nse online` | `nse.player.view` | List online players and ping. |
| `/nse laghere [radiusChunks]` | `nse.diagnostics` | Local entity/server-load scan. Radius is clamped to 1–12 chunks. |
| `/nse cleanup scan` | `nse.cleanup` | Non-destructive loaded-entity cleanup scan. |
| `/nse cleanup drops` | `nse.cleanup` | Dry-run dropped-item cleanup. |
| `/nse cleanup drops --confirm` | `nse.cleanup` | Remove dropped-item entities only. |
| `/nse maintenance status` | `nse.server` | Show maintenance state. |
| `/nse maintenance on [duration] [reason]` | `nse.server` | Enable maintenance, optionally timed. |
| `/nse maintenance off` | `nse.server` | Disable maintenance. |
| `/nse shutdown <duration> [reason]` | `nse.server` | Schedule a clean shutdown. |
| `/nse shutdown cancel` | `nse.server` | Cancel scheduled shutdown/restart. |
| `/nse restart <duration> [reason]` | `nse.server` | Schedule clean shutdown intended for external-supervisor restart. |
| `/nse restart cancel` | `nse.server` | Cancel scheduled shutdown/restart. |
| `/nse undo` | `nse.player.manage` | Undo the latest reversible suite admin action. |

Common duration forms used by the admin service include values such as `30s`, `10m`, `6h`, `3d`, and `2w`. `permanent` is accepted only by operations that explicitly support permanent state.

---


## Integrated AFK mode

```text
/afk
/nse afk
/afkstatus
/afkinfo <player>
/nse afkinfo <player>
```

`/afk` toggles the caller's locked AFK mode and requires the custom `afk` privilege. If another mod owns root `/afk`, `/nse afk` remains available. `/afkstatus` shows the caller's AFK subsystem/debug status. Staff use `/afkinfo <player>` (or `/nse afkinfo <player>`) for player status.

The Suite is still optional on clients. AFK locking/protection is server-authoritative; the orbit camera is enabled only for clients that also have the matching Suite build and complete the AFK camera handshake. Remove the old standalone `newfiesafk` mod when using the integrated subsystem.

---

## Convenience administration roots

These route into the same administration service.

```text
/warn <player> <reason>
/warnings <player>
/unwarn <player> <warningId>
/tempban <player> <duration> <reason>
/baninfo <player>
/mute <player> <duration|permanent> <reason>
/unmute <player>
/muteinfo <player>
/freeze <player> [duration|permanent] [reason]
/unfreeze <player>
/goto <player>
/bring <player>
/tphere <player>
/back
/sendback <player>
/invsee <player>
/invremove <player> <inventoryId> <slot> --confirm
/invsnapshot <player> [reason]
/invrestore <player> <snapshotId> --confirm
/invgive <player> <code> <qty>
/invqty <player> <inventoryId> <slot> <qty>
/invmove <player> <fromInv> <fromSlot> <toInv> <toSlot>
/invreplace <player> <inventoryId> <slot> <code> <qty>
/heal <player>
/feed <player>
/revive <player>
/seen <player>
/playtime <player>
/afk
/afkstatus
/afkinfo <player>
/online
/history <player>
/note <player> add <text>|list|remove <id>
/nseundo
/laghere [radiusChunks]
/maintenance on [duration] [reason]|off|status
```

Conditional convenience roots:

```text
/report <issue>
/shutdown <duration> [reason]|cancel
/restart <duration> [reason]|cancel
/back
```

`/shutdown`, `/restart`, `/back`, `/spawn`, `/msg`, `/r`, `/starterkit`, `/setstarterkit`, and the starter-kit reset roots are not registered by the suite when Th3Essentials compatibility mode is active. Under Stratum, only the root `/restart` alias is suppressed; `/nse restart` remains available. NSE also skips root `/restart` whenever another provider already registered it.

---

## BlockLogger

Privilege: `blocklogger`

### Administration

```text
/blocklogger status
/blocklogger reload
/blocklogger reloadconfig
/blocklogger path
/blocklogger count
```

### Block/container lookup

```text
/blocklog
/blocklog help
/blocklog up
/blocklog down
```

Options:

```text
-r <radius>
-n <rows>
-s <search text>
-t <tag/action>
```

Examples:

```text
/blocklog -r 3 -n 100
/blocklog -s chest
/blocklog -t break
/blocklog up -r 1
```

Supported action/tag filtering in storage includes block break/place/use, container open/take/put, and entity interact/spawn/death/despawn where relevant to the query type.

### Entity lookup

```text
/entitylog
/entitylog -r <radius> -n <rows> -s <text> -t <tag>
```

Exact looked-at entity alias:

```text
/blockloge
/blockloge -n <rows> -s <text> -t <tag>
```

Requested row counts are still clamped by the server BlockLogger configuration.

---

## Animal Limiter

Privilege: `controlserver`

```text
/animallimit help
/animallimit status
/animallimit all on|off
/animallimit default <limit>
/animallimit radius <0-32 chunks>
/animallimit interval <reconciliation-seconds>
/animallimit set <entityid|wildcard> <limit> [radius]
/animallimit target <limit> [radius]
/animallimit look <limit> [radius]
/animallimit remove <pattern>
/animallimit exclude <pattern>
/animallimit include <pattern>
/animallimit protect pets|farm|named on|off
/animallimit generation off|wildonly|bredonly|protectbred
/animallimit list
/animallimit find <text>
/animallimit matches <pattern>
/animallimit top [pattern]
/animallimit preview [pattern] [limit]
/animallimit cull
/animallimit history [count]
/animallimit return last
/animallimit return <removal-id>
/animallimit reload
```

Notes:

- `interval` has a minimum of 60 seconds and is capped at 3600 seconds.
- `target` and `look` use the entity currently under the staff player's crosshair.
- `preview` is non-destructive.
- `return` attempts to restore a recorded Animal Limiter removal.
- Per-rule radius is 0–32 chunks.

---

## Login Protection

Privilege: `controlserver`

```text
/loginprot status
/loginprot on
/loginprot off
/loginprot <seconds>
```

The duration is clamped to 1–3600 seconds.

---

## Anti-Cheat / Client Integrity

Privilege: `controlserver`

The root is registered only if `/anticheat` is not already owned by another mod.

```text
/anticheat status
/anticheat status <player>
/anticheat rescan <player>
```

Packet Protection settings do not currently have separate chat toggle commands. Configure them through **Shift+L → Anti-Cheat → Packet Protection**, the WebPanel, or `NewfiesAntiCheat.json`.

---

## Web Dashboard / Server Utilities

Privilege: `controlserver`

```text
/serverutils web status
/serverutils web on
/serverutils web off
/serverutils web bind <ip>
/serverutils web port <1-65535>
/serverutils web publicurl <url>
/serverutils web publicurl off
/serverutils web username <name>
/serverutils web password <password>
/serverutils web sessions
/serverutils web logoutall
/serverutils web reload
/serverutils web securecookies on
/serverutils web securecookies off
```

A non-loopback listener should not be exposed without authentication and appropriate network security.

---

## Web Map player commands

Privilege: ordinary `chat` privilege.

The root is registered only when `/webmap` is not already owned by another mod.

```text
/webmap help
/webmap waypoint add major [icon:<type>] <name>
/webmap waypoint add minor [icon:<type>] <name>
/webmap waypoint remove <name>
/webmap waypoint list
/webmap live off
/webmap live on
/webmap live status
```

`wp` is also accepted as the waypoint subcommand alias:

```text
/webmap wp add minor Home
```

Removal aliases accepted by the handler:

```text
remove
delete
del
```

List aliases accepted by the handler:

```text
list
count
```

Waypoint icon types:

```text
pin
base
settlement
shop
mine
resource
danger
landmark
road
farm
```

Examples:

```text
/webmap waypoint add major icon:base Main Base
/webmap waypoint add minor icon:mine Copper Mine
```

---

## Player reports

When the root is available:

```text
/report <issue>
```

The suite route remains:

```text
/nse report <issue>
```

Staff management is documented under the `/nse reports...` command family above.

---

## In-game Investigator

The Investigator is a GUI rather than a chat command.

Default hotkey:

```text
Shift+L
```

Its available sections depend on the enabled feature gates and installed client-side suite support.

## Client controls

- **Tab (hold)** — Show the public online player list; release to close. Rebindable as **Show Newfies public player list** in Controls. Available to every player.
- **Shift+L** — Open the existing Server Essentials administration/investigator GUI (server privileges still apply to admin functions).

---

## Vintage Story character-selection interoperability

Vintage Story provides the underlying one-time character-selection permission:

```text
/player <player> allowcharselonce
```

Server Essentials Player Management uses this mechanism for its **Allow Character Selection Once** action. Online players are updated/synchronized directly; offline known players use the server-console player command path. The player then runs `.charsel` client-side.

## /disguise (admin)

`/disguise <entity> [visible]` turns you into an entity, `/disguise off` removes it, `/disguise list [part of a name]`
searches the registered entity types. Needs the `nse.player.manage` privilege.

Vintage Story cannot change another player's model from the server, so the disguise is a real entity that follows your
position and facing every 100 ms while you are hidden in ghost mode (spectator, so you cannot interact while disguised).
`visible` keeps you un-hidden, which shows both your own model and the entity. The entity is removed when you use
`/disguise off`, disconnect, or the server stops, and any left behind after a crash are swept shortly after the next start.
Names can be partial (`chicken-hen`); an ambiguous name lists the matches instead of guessing. Every use is audit-logged.

While you move, the disguise plays `idle`, `walk` or `run` to match your speed (entities without those animations simply
stay still). Other players cannot hit, mount or use the disguise. If some online players run a client without
ghost-hide support, `/disguise` tells you how many may still see your real model next to it.

Limits: environmental damage can still affect the entity, and a hostile type may try to act until its AI is removed (the
Suite removes it where the game allows). Treat this as an admin tool, not a security boundary.
