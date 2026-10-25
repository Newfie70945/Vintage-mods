# WebMap Guide

Server Essentials **1.2.35**

## Two separate switches

WebMap has two levels of enablement:

1. the `webmap` **feature gate**, which loads the suite module/commands/settings;
2. the WebMap runtime `Enabled` setting in `NewfiesWebMap.json` / WebPanel / Shift+L.

A feature gate can be available while the public map listener remains off.

Check feature gates:

```text
/nsefeatures list
```

Enable the module if required:

```text
/nsefeatures webmap on
```

Restart after changing feature gates.

## Player-facing commands

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

See [COMMANDS.md](COMMANDS.md) for aliases and icon types.


## Community leaderboards

Server Essentials exposes community progression leaderboards in two places: an authenticated **Leaderboard** button in the WebPanel top bar, and (when enabled) the public Web Map **Leaderboards** view. Both use the same cache and provide three tabs:

```text
Overview
Skills
Playtime
```

The default view includes Suite-tracked total playtime, current session time, earliest first-seen veterans, total XSkills levels, and derived cumulative XSkills XP where the data is available. Player names are clickable and open a small public progression profile with playtime, first/last seen, overall skill totals, and top skills.

### XSkills / XLib compatibility

XSkills support is optional. Server Essentials discovers `XLib.XLeveling.XLeveling` at runtime and reads the registered server-side skill set through reflection. There is **no hard XLib or XSkills project reference**. If XSkills/XLib is not installed, skill cards and XSkills totals are omitted while Suite playtime leaderboards continue working.

The cache merges both live `PlayerSkillSets` and persisted/disconnected `DiscPlayerSkillSets`. That means players do not disappear from XSkills rankings when they log out. If an old XSkills record is not yet present in Suite player history, its UID is used only internally to resolve Vintage Story's last-known player name; the UID itself is never placed in the leaderboard payload.

The Skills tab automatically follows the skills registered by XLib/XSkills instead of hard-coding a required list. Common skills receive matching icons/accent colors, while unknown/custom skills receive a neutral default card.

### Ranking cache

Leaderboard generation is cached and can be configured in the WebPanel:

```text
Enable Leaderboards
Show Playtime
Show XSkills
Top N players (3-20)
Refresh interval (30-300 seconds)
```

XSkills reflection is performed on the server/game thread. WebPanel and public HTTP requests only serialize the cached leaderboard snapshot, so repeatedly opening either surface does not rescan every player on the HTTP listener thread.

The authenticated WebPanel leaderboard cache is independent of the public `Show Leaderboards` switch. Disabling the public leaderboard hides its public endpoint/view without disabling the staff-authenticated WebPanel leaderboard.

### Public-data boundary

Leaderboard output is intentionally restricted to community-facing progression data. It does **not** publish player UIDs, IP addresses, Anti-Cheat / Client Integrity status, moderation actions, warnings, staff notes, or other private administration fields.

The existing `/webmap live off` preference also applies to leaderboards: a hidden player is omitted from public leaderboard rankings/profiles. Online badges and current-session rankings only use the already privacy-filtered public player cache.

## Terrain export layers

The exporter can publish the terrain styles independently:

```text
Normal color
Sepia / parchment
Topographic
```

You do not need to generate every style. Disable styles you do not use to reduce export work/storage. At least one terrain style should remain enabled for a usable map.

## Full export workflow

Recommended production flow:

1. Announce maintenance/export to players.
2. Start the terrain export from the WebPanel/Shift+L control you normally use.
3. During the blocking export phase, the suite can disconnect game clients with:

```text
WebMap export in progress. Please reconnect in a few minutes.
```

4. Wait for export finalization.
5. Verify the selected terrain layers exist and load.
6. Allow players to reconnect normally.

The disconnect behavior is intended to avoid clients appearing stuck at 0 KB while the server is occupied by the export.

## Export-time entity error suppression

Vintage Story may log repeated chunk entity reconstruction errors during a large export, for example temporary spell entities or entities from mods that can no longer deserialize cleanly.

During the terrain export only, the suite filters the exact noisy engine pattern for:

```text
Failed loading an entity (...) in a chunk. Will discard...
```

Unrelated server errors are not hidden. After export, the filter is inactive again and a compact suppression summary can be emitted instead of hundreds of duplicate lines.

If the same entity-load errors happen during normal gameplay, investigate the underlying mod/entity data rather than relying on the export filter.

## Live-player privacy

Players can control their live map visibility with:

```text
/webmap live off
/webmap live on
/webmap live status
```

This affects the public live-player layer, not staff/admin tracking systems.

## WebMap language support

The public WebMap supports:

```text
English
German
French
Russian
```

Browser language can be detected automatically, and the user can select/remember a language in the map UI.

Technical map values such as coordinates, block codes, icon IDs, and internal identifiers are not translated.

## Security

Treat the public map and the WebPanel as different surfaces. If you expose the WebPanel, use authentication and preferably a reverse proxy with HTTPS. Do not assume WebMap settings automatically secure the admin panel.

## Troubleshooting

If a terrain layer is missing:

1. confirm that style is enabled;
2. confirm the export completed;
3. refresh the browser cache;
4. check exporter/log output;
5. confirm the published/cache directory contains the expected style pyramid.

If players cannot join long after export completed, verify the export state cleared and check the server log for the export finalization path.

## Direct leaderboard URL

When public leaderboards are enabled, the leaderboard can be linked directly at:

`<PublicMapUrl>/leaderboard`

This route opens the privacy-sanitized public leaderboard without starting the normal map/status/terrain polling loop, making it suitable for links from Community MOTD, Discord, or a website.

The authenticated WebPanel also supports its own deep link at `<WebPanelUrl>/leaderboard`; that route follows normal WebPanel authentication and exposes the richer admin view.

### Player icon refresh

WebPanel → Web Map → **Player icon refresh** controls the live player snapshot and browser polling interval. The persisted `NewfiesWebMap.json` field is `PlayerIconRefreshSeconds`; default **60**, range **1–300 seconds**. Existing configs retain 60 seconds. For example, use 5 for more frequent updates. Changes apply to the running service and open maps without a restart or reload.

Player polling uses the lightweight `/api/players` endpoint and cached, privacy-filtered positions. Full map metadata still refreshes every 30 seconds; terrain and activity are not regenerated for faster icons. Game-thread captures run only while viewers are active, requests are coalesced, and hidden browser tabs pause player polling. A newly opened map can briefly show the last cached snapshot while a fresh game-thread capture is queued. Faster values increase player capture and HTTP traffic. The Players layer and live-data privacy gate must both be enabled; `/webmap live off` remains respected.

## Translocator layer — shared discoveries

Enable **Web Map → Public layers → Translocators**, then **Save Settings**. This is off by default because discovered pad coordinates become public. All map viewers see shared community discoveries, including pads created or repaired by players in creative mode. Player names and UIDs are not in this layer or its discovery file.

- A loaded official static translocator is discovered through nearby observation, looking at it within the configured radius, placing it or interacting with it. Spectators are excluded. Discovery radius is 3–16 blocks (default 6), measured in 3D in the overworld.
- Broken pads appear as broken; repairing an observed pad refreshes its state. A saved destination is only published when a separate observation has discovered that destination and both pads are repaired. An undiscovered destination's coordinates are absent from the API JSON, not just hidden by the browser. No return link is invented.
- Movement jumps, `/tp`, warps and respawns are not used as evidence of a link. Use totals and discoverer counts are not fabricated or displayed.
- Polling handles one player per 250 ms turn with a shared ceiling of 128 loaded-block probes and a 2 ms soft budget. A single engine call can exceed a soft budget. Stationary-player cursors advance across turns; players take turns fairly. Looking/interaction helps discover pads before a whole local volume is inspected.
- No terrain generation, chunk loading, target-area reads or full-world scans. Only loaded terrain near actual players is inspected. Created/repaired pads are picked up while this layer is enabled; previously visited pads become known when revisited after enabling.
- JSON loading/serialization/writing uses one background worker, coalescing changes for about a second and replacing the file through a temporary file. The worker is retained across live toggles; disabling tracking removes tick/event handlers and keeps discoveries. Shutdown allows a bounded final flush. A failed write is retried and shown in the WebPanel status; normal game saves are not modified.
- Records are bounded to 5,000, saved in `translocators-v2-<world-hash>.json` under the map's data directory, and scoped to the savegame identity. The old proposed v1 file is not imported because its inferred links/discovery provenance were not reliable. Public feed caching means map markers may take the normal metadata refresh interval to appear.
