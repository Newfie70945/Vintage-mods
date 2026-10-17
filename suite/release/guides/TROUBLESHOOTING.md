# Troubleshooting Guide

Server Essentials **1.2.27**

Use this as the first diagnostic pass before changing thresholds or deleting config files.

## Shift+L buttons show backwards/upside-down text

This was caused by a negative font-size path in the translation auto-fit code. 1.2.15 includes the guarded orientation/fit fix.

Check:

1. you are actually running 1.2.15;
2. no older Server Essentials DLL/assets remain in the mod directory;
3. restart the client after replacing the mod;
4. if it persists, capture a screenshot plus the active client language and GUI scale.

## Packet Protection toggles do not stay enabled

Affected settings included player chat warnings and block-collision/noclip detection.

1. Use 1.2.15 or later.
2. Open **Shift+L → Anti-Cheat → Packet Protection** or the WebPanel.
3. Change the toggles.
4. Save.
5. Reopen/reload the page.
6. Confirm the UI reports the persisted values.

The current save path stamps Anti-Cheat schema v47 before normalization so old one-time migrations cannot silently force the toggles off again.

## `allowcharselonce` reports `Tungsten command returned NoSuchCommand`

The old WebPanel action routed through the wrong generic/Tungsten command execution path.

Current behavior:

- online player: Server Essentials directly sets/synchronizes `allowcharselonce`;
- offline known player: the suite uses the Vintage Story server-console `/player <name> allowcharselonce` path.

The player can then run `.charsel` client-side.

## Anti-Cheat movement false positives

Do not immediately raise global movement limits.

Check the evidence for:

```text
unknown/modded mount profile
teleport-sized position delta
very short sample time
TPS/tick hitch
stationary block overlap
mount seat/rider vertical resync
```

Then read [ANTICHEAT.md](ANTICHEAT.md).

## A player is kicked but the disconnect screen says `unknown`

1.2.15 includes the shared disconnect compatibility helper that supplies both the public kick message and the player-facing reason through the server disconnect path.

If `unknown` still appears, capture:

```text
which subsystem kicked the player
full server log around the kick
player-facing pre-kick chat message
Vintage Story server build
```

## WebMap export spams entity-load errors

During export only, the suite suppresses the exact repeated Vintage Story entity-load failure pattern and summarizes it afterward.

If you still see unrelated errors, they are intentionally not being hidden. If the same entity error appears outside export, investigate the responsible entity/mod/save data.

## Clients appear stuck at 0 KB during WebMap export

Blocking exports can temporarily disconnect players with a clean export-in-progress reason. If clients still hang instead of receiving the disconnect:

1. confirm you are running the current server build;
2. verify the export reached the blocking/server-lock phase;
3. check for exceptions before the disconnect path runs.

## Missing command

1. Run `/nsefeatures list`.
2. Restart after changing feature gates.
3. Check privileges.
4. Check startup logs for command-owner conflicts.
5. Remember that the suite intentionally skips roots already owned by another mod.

Examples:

- Th3Essentials can own `/restart`, `/shutdown`, and `/back`.
- Stratum can own `/restart`.
- another mod may already own `/report`, `/webmap`, or `/anticheat`.

Use `/nse ...` suite routes where applicable.

## WebPanel does not load

Check:

```text
/serverutils web status
```

Then verify:

```text
bind address
port
firewall
reverse proxy
public URL
password/authentication
secure-cookie setting when using HTTPS
```

Prefer `127.0.0.1` behind a reverse proxy.

## Translation text clips or overlaps

1.2.15 includes the long-label auto-fit/wrapping pass for EN/DE/FR/RU.

If one control still clips, report:

```text
language
exact page/tab
button/label text
GUI scale or browser zoom
screenshot
```

Do not shorten technical identifiers just to make them fit; those are intentionally kept exact.

## BlockLogger queue keeps growing

Run:

```text
/blocklogger status
```

Watch pending rows, peak queue, drops/overflow, writer timing, and SQLite errors. Persistent growth usually points to storage throughput/locking rather than a need for larger queues.

## Memory keeps growing

Use the Memory/Performance Center and record slopes for:

```text
process private memory
managed heap
GC committed/fragmented
loaded chunks
loaded entities
BlockLogger queue pressure
Animal Limiter tracking
```

Capture diagnostics before restarting the server.

## What to attach to a bug report

Provide the smallest reproducible bundle:

```text
Server Essentials version
Vintage Story version
relevant companion versions
exact timestamp
server log excerpt
relevant config section
steps to reproduce
screenshot/report evidence when UI or Anti-Cheat related
```
