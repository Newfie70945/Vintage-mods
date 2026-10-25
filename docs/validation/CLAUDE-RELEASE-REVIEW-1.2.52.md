# Claude branch release review — Suite 1.2.52

Reviewed 2026-10-25. Branch tips were fetched again before packaging and were unchanged. Main is the integration destination; Claude's branches are preserved. This work does not publish to Mod DB or change a production server.

## Review coverage

- `claude/optimizer-mod-audit-m5la11` at `12f9521`: all three commits, including co-authored Claude changes. Previously integrated safeguards were rechecked in the full current build.
- `claude/motd-mod-gui-8g2605` at `0554fde`: both commits, plus corrections to first-line links, fences, lists and quotations.
- `suite-performance-improvements` at `65ee115`: all seventeen commits. `ccr-1fb2c0f4-jz5owu` at `3b1154e` duplicates the first three; its useful changes were not applied twice.
- Remote-history search found no additional unique Claude-authored branch changes outside this set. Cherry-picked original commits still appear outside main by ancestry; the table below records their disposition.

## Suite branch disposition

| Original commit | Disposition | Reason / result |
| --- | --- | --- |
| d43f3bc | Accepted | Cache AFK member metadata, read player values live. |
| 800ba87 | Held | Offline discovery eviction sorts and removes potentially all records on a game tick under a shared lock, without an incremental budget. |
| 3b1154e | Accepted | Avoid a BlockPos allocation per ore-mask lookup; coordinate/dimension behavior retained. |
| 7b9701d | Accepted | Reuse compiled whitespace regex and timestamp formats. |
| 032c8a0 | Accepted with correction | Recurring-mod patterns now require retained measured profiler evidence, not confidence labels alone. |
| 5f89e3e | Held | Timeout ends waiting but does not interrupt synchronous SQLite aggregation; subsequent passes can overlap abandoned readers. |
| 59a4cad | Held | Live log snapshot performs up to 512 KiB of synchronous reading/parsing on the game thread. |
| 7f561bf | Held | Callback-to-callback process GC pause deltas cannot prove overlap with the previous profiler root; misleading confirmed attribution. |
| f8969ea | Accepted | Bound anti-cheat ZIP fingerprint decompression against declared entry length. |
| 77cacf3 | Partly accepted | Keep optional NOS lifecycle timing and companion-owner policy. Remove/defer SuiteBurstTimer and wiring: final bursts can remain unreported and later calls can combine old and new windows. |
| 7240380 | Accepted with correction | Offline findings analyzer; retained-history GC uses observed window, missing/nonfinite GC data stays unavailable. |
| 338e9e7 | Accepted with correction | Offline A/B tool; normalize GC deltas by actual cadence, refuse a verdict with missing workload metadata. |
| e838aa1 | Accepted | Authoritative Discord player roster, character-selection command and staff input limits. |
| 86718fc | Accepted with correction | Dependency/game-version display and version-specific ignores. Persist candidate before changing in-memory ignore state. |
| a82074e17 | Accepted | Distinct vanilla rock map colors; existing cached tiles need rebuilding to adopt new colors. |
| 9c16725 | Held | Publishing a discovered pad's target exposes undiscovered destination coordinates. Main-thread scans and JSON persistence also lack a small work budget; inferred links can be incorrect. |
| 65ee115 | Accepted | Acknowledge unchanged rendered chunks independently of newer edits elsewhere; choose oldest-rendered tiles first. Automatic exploration still preserves unsaved dirty edits. |

The previous detailed review is in `CLAUDE-BRANCH-AUDIT-2026-10-25.md`. No withheld feature is advertised as shipped.

## Public release baseline and versions

The public Suite 1.2.51 assembly's informational version is `1.2.51+bce3580f64a073941365fde28bfc96f0b4bf42da`. That exact source commit is the Suite changelog baseline. Its official ZIP SHA-256 is `2ad50f88b558fbcfb2619a514df55c96c47a8d8eeb82e0ed31f32e42dcd51371`.

Companion releases were checked against their current Mod DB metadata and downloaded DLLs: MOTD 2.1.6, Discord Link 1.0.14, Anti-Cheat Client 1.1.27, ChatGuard 2.1.4 and Player List 1.1.2. Comparisons accounted for Debug/Release compiler differences; recompilation alone is not a feature change.

Changed releases: Suite 1.2.52, MOTD 2.1.7, Discord Link 1.0.15 and Anti-Cheat Client 1.1.28. NOS 0.20.14-preview remains an unpublished engineering preview: no public Mod DB baseline was verified. ChatGuard and Player List are unchanged; the compiled pack retains their byte-exact official release ZIPs rather than publishing different binaries under the same version.

## Validation

- Six ordinary modules compile against official Vintage Story 1.22.7 and .NET 10 with zero compiler warnings/errors.
- NOS build/test driver passes all **35** component/Python suites, including 156 Harmony module lifecycle/conflict checks, the three exact chunk lifecycle targets, companion-owner policy, and 57 offline analysis/comparison tests. Heavy modpack/server benchmark projects are excluded by the existing driver; this is not a new capacity benchmark.
- An initial strict AI task-start allocation assertion failed during concurrent build/decompilation work. An isolated default-runtime retry and the subsequent complete driver both pass with zero optimized allocation in all five trials. The assertion was not weakened. Timing results vary and are not production TPS claims.
- 42 shared formatter checks and 30 Discord event/compiled-provider checks pass.
- Compiled Suite tests pass dependency/ignore expiry and injected durable-write failure (memory/display remain unchanged).
- Map tests pass detached decoding/disposal, preservation of block/fluid IDs and live-save integrity, per-chunk dirty acknowledgement, bounded new saved exploration and error fallback. No live world-loader calls are used for these exports.
- Seven browser regressions pass: Performance live workspace, navigation, timeline recovery, incremental map tiles, map player refresh, optimizer controls and proximity chat.
- Disposable official server integration passes with Suite and NOS together: capture start/stop through the Suite, all three lifecycle targets retained, 32 HTTP header requests across 16 workers, Performance endpoints, proximity group/settings persistence, and opt-in map settings. Shutdown is clean and the copied world passes SQLite integrity checks. This empty-server test is not a production load benchmark.
- `tools/check_sources.py` and whitespace checks pass. Distribution hashes are recorded in the delivered manifest files.

No test here proves every third-party mod compatible or guarantees production tick improvements. Optional runtime modules and detailed capture settings keep their existing defaults.
