# Newfie's ChatGuard 2.1.0

## Moderation commands
- Added compact mute durations: `30m`, `2h`, `3d`, `1w`; plain numbers remain minutes.
- Added permanent mutes via `perm`, `permanent`, or `forever`.
- Added `/cgmutes [count]`.
- Added `/cgwarnings <player|uid>`.
- Added `/cgclearwarnings <player|uid> [reason]`.
- Added `/cginfo <player|uid>`.
- `/cgunmute` now accepts an optional reason and resolves persisted mute/warning records by player name or UID.
- Added `/chatguard help`.

## Persistence and integration
- Permanent mute state is persisted explicitly.
- Expiration cleanup skips permanent mutes.
- Event-bus snapshots now include `warningsJson` for Server Suite/in-game GUI consumers.
- Manual mute operations now increment the same mute counter used by ChatGuard status/telemetry.

## Compatibility
- Retains the 2.0.0 BuildFix1 Vintage Story 1.22.7 event-bus unregister compatibility shim.
- Retains `AllOnlinePlayers.OfType<IServerPlayer>()` server-player filtering.
