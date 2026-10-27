# Tested release candidate — October 27, 2026

Packages: Suite 1.2.53, NOS 0.20.18-preview, MOTD 2.1.8, Anti-Cheat Client 1.1.29,
ChatGuard 2.1.6, Discord Link 1.0.16 and PlayerList 1.1.3. The five companion bumps
identify the icon/packaging updates; their implementation changes are release
identity strings only. Target: official Vintage Story 1.22.7 / .NET 10.

## Executed checks

- All six Suite/companion builds: zero warnings and errors.
- NOS build and all 38 registered test suites: passed, including 169 engine
  lifecycle/compatibility checks, 33 dropped-item checks with a 3,000-drop
  conservation fixture, barrel memoization and block-update diagnostics.
- Nineteen compiled regression suites: passed after updating the package fixture
  described below. Coverage: packet ingress policies, client reports, coalesced
  snapshots, native ingress, audit follow-up, 41 collision/glider checks, combat
  and server actions, applied packet settings, speed risk/disconnect gates,
  OreShield transport, read timeouts, terrain rendering, export buffer ownership,
  webmap behavior, translocator rules, companion lifecycle/PNG checks, client
  package hashing/path caches and official package loading/SQLite.
- Nineteen browser checks: passed after updating the two fixtures described below.
  Coverage: workspace polish, refresh races, automatic scheduler, timeline,
  hidden/reveal/resize recovery, incident navigation, Packet Protection, NOS,
  BlockLogger recording, sidebar status/transport, header evidence, incremental
  map tiles, public player updates, translocator layer, profiler controls,
  navigation request pressure, panel lifetime, memory rate and MOTD editor.
- Official ModContainer/ModAssemblyLoader checks for all six Suite/companion ZIPs:
  passed with game SQLite loaded first. Metadata and assembly versions match.
- Archive checks: valid ZIPs, root PNG icons, metadata versions and packaged DLL
  hashes match the build outputs. NOS ZIP matches its build manifest.
- Source consistency and whitespace checks: passed.

## Test maintenance found during this run

Initial failures are retained in the accompanying logs rather than omitted:

- The old package fixture required a bundled SQLite 3.53.3 dependency. Current
  Suite intentionally uses the official game's SQLite. The fixture now checks
  official managed/native resolution, absence of competing packaged providers,
  and actual in-memory create/insert/read; legacy package checks remain. The
  corrected check passed using the official SQLite 3.49.1 runtime.
- The isolated navigation fixture extracted the refresh function without its new
  static-status cache declaration. It now includes that production declaration;
  sequential request, coalescing, failure and logout checks pass.
- The chart-recovery fixture could install its fake owner session before the
  initial unauthenticated session response completed. That late response could
  hide the app, leaving the canvas at zero size. The fixture now settles initial
  network work before installing its session. Five consecutive corrected runs
  passed. No graph assertion was removed or weakened.
- The native integration test hardcoded the previous ChatGuard version. It now
  derives the expected version from the release manifest. The complete corrected
  integration passed with ChatGuard 2.1.6.

These corrections change test setup/expectations, not shipped Suite behavior.

## Official-server integration

Run: `artifacts/validation/web-snapshot-1793144780/result.json`.

All seven mod IDs loaded. Authenticated reading/settings checks, first expired
Performance snapshot freshness, compiled workspace HTML/capture timestamps,
server protection/exemption/export checks, optimizer profiling/settings round
trips and translocator/public map settings checks passed. Explicit save and
shutdown completed; exit code 0, server errors 0, SQLite save integrity `ok`.
The tested Suite DLL and installed NOS ZIP match the final packaged artifacts.

This uses an isolated disposable world with no authenticated gameplay clients.
Real-player glider takeoff/landing and representative full-modpack workloads are
still field checks. Results do not establish production TPS gains or universal
mod compatibility. No production world or configuration was modified.
