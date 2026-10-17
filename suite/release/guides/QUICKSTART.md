# Quick Start — 10-minute production checklist

Newfie's Server Essentials Suite **1.2.27**  
Target: **Vintage Story 1.22.7 / .NET 10**

Use this when you already know how to install a Vintage Story mod and just want a safe first boot.

## 1. Install and start once

Build/copy Server Essentials into the server `Mods` directory, then start the server once so the suite creates its configuration files in `ModConfig`.

The main suite is server-required but remains client-optional for normal players. Staff who use the **Shift+L** administration GUI should install the matching Server Essentials build on their client. The integrated `/afk` feature also works server-side without a client install; installing the matching Suite client enables its optional orbit camera.

## 2. Check enabled features

```text
/nsefeatures list
```

If you enable or disable a feature gate, restart the server after saving so listeners, timers, workers, and network hooks are rebuilt cleanly.

## 3. Verify the core services you use

```text
/blocklogger status
/loginprot status
/animallimit status
/anticheat status
/serverutils web status
/afkstatus
```

Only run the commands for features you enabled.

## 4. Open the staff GUI

Press:

```text
Shift+L
```

Useful areas include:

- Player Administration
- BlockLogger / Investigator
- Anti-Cheat
- Anti-Cheat → Packet Protection
- Profiler / diagnostics
- WebPanel settings
- WebMap settings/export
- Animal Limiter

## 5. PacketGuard first-check

In **Shift+L → Anti-Cheat → Packet Protection**:

- confirm whether player chat warnings should be enabled;
- confirm whether block-collision/noclip detection should be enabled;
- review the false-positive hardening controls before changing them;
- save and verify the UI reports the persisted values.

Block-collision/noclip detection is intentionally conservative on heavily modded servers because custom collision shapes can produce false positives.

## 6. WebPanel safety

For a browser admin panel, start on loopback:

```text
/serverutils web bind 127.0.0.1
/serverutils web password <strong-password>
/serverutils web on
```

Prefer a reverse proxy with HTTPS instead of exposing the listener directly.

## 7. WebMap safety

The WebMap feature gate and WebMap runtime are separate. Large terrain exports should be scheduled intentionally. During a blocking export the suite can temporarily disconnect players with the clean export-in-progress message rather than leaving them stuck at 0 KB.

See [WEBMAP.md](WEBMAP.md).

## 8. Back up before tuning

Back up at least:

```text
world save
ModConfig
suite persistent admin/report data
BlockLogger database
```

Then keep a copy of the working `NewfiesAntiCheat.json` before major Anti-Cheat tuning.

## 9. Know the separate companions

- **AntiCheat Client 1.1.26** — install on server/clients when your Anti-Cheat policy requires the client companion.
- **Player List 1.1.1** — optional hold-Tab public roster.

## 10. If something is wrong

Start with [TROUBLESHOOTING.md](TROUBLESHOOTING.md), then gather the exact server log line, the relevant config section, and the affected player/report evidence before changing detector thresholds.
