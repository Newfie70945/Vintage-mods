# Newfie's Server Essentials - Discord 1.0.8

- Fixed `/players` reporting `0` while players are connected by using a resilient server-side live-player snapshot instead of requiring `World.AllOnlinePlayers` entries to be exactly `Playing`.
- `/serverinfo` uses the same live-player snapshot.
- `/date` and `/serverinfo` now display proper in-game `HH:mm` time.
- `/ban` now requires a Suite temporary-ban duration (`30m`, `6h`, `3d`, `2w`, etc.).
- `/mute` now requires a duration and accepts `permanent`.
- The paired Server Suite handles `/ban`, `/unban`, `/mute`, and `/unmute` through the existing event bus and returns the real Suite result immediately.

- Added staff-only Discord `/whitelist` with `Add` and `Remove` actions.
- Whitelist changes execute Vintage Story's native `/player <name> whitelist on|off` command as the server console.
- `/whitelist` uses the existing Discord staff authorization and Admin Audit path.
