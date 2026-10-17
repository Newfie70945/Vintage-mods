# Staff Workflows

Newfie's Server Essentials Suite **1.2.27**

This file is task-oriented. Exact command syntax is in [COMMANDS.md](COMMANDS.md).

## Investigate grief or theft

1. Get the location and approximate time.
2. Look at the affected block/container.
3. Open `Shift+L` Investigator or use `/blocklog`.
4. Start with a small radius/row limit.
5. Expand only if needed.
6. Correlate with player history.
7. Record important findings in a private staff note.

Useful commands:

```text
/blocklog -r 1 -n 100
/nse history <player>
/nse note <player> add <finding>
```

For an exact looked-at entity use `/blockloge`; for nearby entity history use `/entitylog`.

## Correct an inventory safely

Before editing:

```text
/invsee <player>
/invsnapshot <player> Before staff correction
```

Use the narrowest edit possible:

```text
/invqty ...
/invmove ...
/invreplace ...
/invremove ... --confirm
```

Restore only when necessary:

```text
/invrestore <player> <snapshotId> --confirm
```

The browser/Shift+L Player Management UI uses normal confirmation dialogs instead of long typed safety phrases for its supported actions; CLI commands that explicitly document `--confirm` still require it.

## Handle reports

Player:

```text
/report <issue>
```

Staff:

```text
/nse reports open
/nse reportassign <id> me
/nse reportstatus <id> investigating
/nse reportnote <id> <private note>
/nse reportstatus <id> resolved <resolution note>
```

Use BlockLogger/admin history as evidence instead of relying only on chat claims.

## Temporary moderation

```text
/warn <player> <reason>
/mute <player> 30m <reason>
/tempban <player> 3d <reason>
/freeze <player> 10m <reason>
```

Check/remove with the corresponding info/unmute/unban/unfreeze commands.

## Re-open character selection for a player

Use the Player Administration/WebPanel action for **Allow Character Selection Once**. For an online player the suite sets and synchronizes the watched attribute directly. For an offline known player it uses the Vintage Story server-console player command path.

Then tell the player to run:

```text
.charsel
```

## Review an Anti-Cheat report

Do not judge from the severity color alone.

Read, in order:

```text
Finding code
Confidence
Risk family + before/after
Position
Evidence / guidance
Nearby/corroborating signals
Consequence
Suppression/diagnostic context
```

For movement reports, specifically check whether the player was:

```text
mounted
teleported
respawning
using a portal/translocator
inside a complex collision block
experiencing a TPS/tick hitch
```

If evidence shows an unknown modded mount with huge vertical speed but almost no horizontal movement, treat rider/seat synchronization as a likely compatibility cause until proven otherwise.

See [ANTICHEAT.md](ANTICHEAT.md).

## Enable PacketGuard chat warnings / noclip protection

Open:

```text
Shift+L → Anti-Cheat → Packet Protection
```

Toggle the desired controls and save. Reopen the page to confirm the values persisted.

For noclip/block-collision detection, test your actual modpack's doors, trapdoors, kilns, carts, mounts, seats, and custom collision shapes before using aggressive consequences.

## Diagnose local server lag

Start small:

```text
/laghere 3
```

Use Performance Center/Profiler for server-wide issues rather than repeatedly increasing the local scan radius.

## Investigate memory growth

Compare trends, not only absolute numbers:

```text
process private memory
managed heap
estimated native/unmanaged memory
GC committed/fragmented
loaded chunks
loaded entities
BlockLogger pending/overflow/drop counts
Animal Limiter tracked entity/region counts
```

Capture a diagnostic snapshot **before** restarting a leaking server.

## Use Animal Limiter safely

Inspect first:

```text
/animallimit find wolf
/animallimit matches caninae:*
/animallimit top
```

Add a targeted rule:

```text
/animallimit set caninae:* 20 3
```

Preview:

```text
/animallimit preview caninae:*
```

Cull only when the preview is correct:

```text
/animallimit cull
```

Review/restore removals when appropriate:

```text
/animallimit history 20
/animallimit return last
```

## Test Login Protection

```text
/loginprot status
/loginprot on
/loginprot 15
```

Then verify a real reconnect and respawn, including any liquid/spawn edge cases relevant to your server.

## Configure a reverse-proxied WebPanel

Recommended topology:

```text
Server Essentials listener: 127.0.0.1:9465
Reverse proxy: public HTTPS endpoint
Firewall: do not expose 9465 directly
```

Configure:

```text
/serverutils web password <strong-password>
/serverutils web bind 127.0.0.1
/serverutils web publicurl https://admin.example.com
/serverutils web securecookies on
```

## Rotate WebPanel sessions

```text
/serverutils web password <new-password>
/serverutils web logoutall
/serverutils web sessions
/serverutils web status
```

## Run a WebMap terrain export

Before a large export:

1. choose only the terrain styles you actually want;
2. tell players there will be a map export;
3. start the export;
4. verify the blocking phase disconnects game clients cleanly rather than leaving them at 0 KB;
5. wait for finalization;
6. verify the published styles in a browser.

Repeated `Failed loading an entity ... in a chunk` spam is filtered only during the export and summarized; unrelated errors remain visible.

See [WEBMAP.md](WEBMAP.md).

## WebMap waypoint workflow

```text
/webmap waypoint add major icon:base Main Base
/webmap waypoint add minor icon:mine Copper Mine
/webmap waypoint list
/webmap waypoint remove Copper Mine
```

Player live visibility:

```text
/webmap live off
/webmap live on
/webmap live status
```

## Planned maintenance/restart

```text
/nse maintenance on 30m Server maintenance
/nse restart 10m Scheduled maintenance
```

The suite performs a clean shutdown. Your host/service/container supervisor is responsible for starting the process again.

Cancel:

```text
/nse restart cancel
```

## Disable unused subsystems

```text
/nsefeatures list
/nsefeatures <feature> off
```

Restart after changing feature gates so runtime hooks are actually removed.

## Troubleshoot a missing command

1. Verify the feature gate.
2. Restart after changing gates.
3. Check privileges.
4. Check startup logs for root-command ownership conflicts.
5. Use `/nse ...` when another mod owns a convenience root.

## Backups

Back up together when practical:

```text
world save
ModConfig
suite persistent admin/report data
BlockLogger SQLite database
```

For SQLite, a clean server shutdown is the simplest safe backup point.

## Related documentation

- [QUICKSTART.md](QUICKSTART.md)
- [SETUP.md](SETUP.md)
- [COMMANDS.md](COMMANDS.md)
- [ANTICHEAT.md](ANTICHEAT.md)
- [WEBMAP.md](WEBMAP.md)
- [UPGRADING.md](UPGRADING.md)
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md)
