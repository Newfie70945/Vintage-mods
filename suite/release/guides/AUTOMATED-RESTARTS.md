# Automated Restarts

Newfie's Server Suite 1.2.27 follows the same restart model used by Th3Essentials: the mod schedules a clean server shutdown, sends countdown warnings, and the hosting environment starts the Vintage Story process again after it exits.

## Configure it

Open either:

- **Shift+L → Player Admin → Server → Automated Restarts**, or
- **WebPanel → Server Tools → Automated Restarts**.

All restart times are UTC. You can configure one time or a comma-separated list of daily times. When the multiple-time list is non-empty, it takes priority over the single time. Warning thresholds are minutes before shutdown, for example `1,2,3,4,5,10,20,30`.

The configuration is stored in `NewfiesAutomatedRestarts.json` and can be reloaded without restarting the game server.

## Commands

- `/restarttime` — shows the time until the next scheduled restart when NSE owns that root command.
- `/nse restarttime` — always uses NSE's scheduler.
- `/nse autorestart status` — show scheduler status.
- `/nse autorestart on` / `off` — enable or disable it.
- `/nse autorestart reload` — reload the JSON configuration.
- `/nse autorestart skip` — skip the next scheduled occurrence.

If Th3Essentials is loaded, NSE deliberately leaves root `/restarttime` to Th3Essentials and keeps `/nse restarttime` as its collision-safe fallback.

## Starting the server again

A Vintage Story mod cannot keep executing after the server process has shut down. Use the restart behavior already provided by your host or panel (Pterodactyl, AMP, systemd, Docker restart policy, etc.).

For a directly hosted Windows or Linux server, example loops are included in `scripts/`:

- `restart-loop-windows.bat`
- `restart-loop-linux.sh`

Edit the server executable and data-path values before using them. These wrappers always relaunch after the process exits, so stop the wrapper itself when you intentionally want the server to stay offline.

## Discord warnings

When **Newfie's Server Essentials - Discord 1.0.0** is installed and connected, enabling **Discord announcements** on the restart page publishes countdown warnings through the Discord module. It uses the configured **Server Stopping** channel when set, otherwise the normal Discord chat channel.

## Shutdown behavior

At the scheduled time NSE broadcasts the final restart notice, then calls Vintage Story's graceful server shutdown API. The restart schedule remains enabled in its JSON configuration, so after the external host/wrapper launches Vintage Story again, NSE calculates the next scheduled occurrence automatically.
