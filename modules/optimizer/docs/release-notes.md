# NOS 0.8.0 preview

- Reuse physics position/animation/tag temporary lists with bounded thread-local storage, nested-call isolation and exception cleanup. Outgoing packet arrays are never pooled.
- Independent PhysicsPacketListReuse switch, compatibility guards, Suite status and work counters.
- Suite 1.2.47 Physics1 clarifies external optimizer availability and preserves older NOS support.
- See performance-0.8.0.md for measured allocation changes and validation limits.

# 0.7.0 preview

- Reduce entity-search garbage through independently controlled nearest/interactable search state reuse.
- Fix worker path requests discarded at the queue processing limit.
- Add optional bounded main-thread pathfinding assistance with a configurable observed-head wait, count cap and soft time budget. Off by default.
- Add stopped-capture reference release through `/optimizer profile clear` and Suite controls; preserve exported reports and refuse active-capture removal.
- Add Suite settings, actual module states and search/path counters. Stock entity tick cadence, AI decisions and A* are retained; no forced GC or global GC-mode changes.
- See performance-0.7.0.md for validation scope. Preview improvements require testing on representative player workloads.

# 0.6.0 engine-patch preview

- Added independent AI task-start callback allocation reduction; stock transitions and all veto callbacks are preserved.
- Added official-method differential, mutation, exception, changed-IL and foreign-owner tests.
- Added a reproducible full-Suite/selected-modpack laboratory runner with real forced-active NPCs, generation, dirty saves, CPU, tick work, allocations, GC and memory, plus separate engine-profiler cost windows.
- Added runtime enabled-mod assertions and explicit failure when the intended moving/AI workload is absent.
- Suite 1.2.44 ModpackStability1 adds opt-in automatic engine captures, safe cancellation/timer ownership, case-tolerant ModDB package validation and an explicit earlier-game-patch compatibility option.
- No production TPS, player-capacity or NOS-versus-Stratum claim. See [measurements and limits](performance-0.6.0.md).

# 0.2.0 engine-patch preview

- Added actual Harmony replacements for online player snapshots and eligible bulk FastMemoryStream reads.
- Added population-adaptive nearby-query temporary-list reuse, leaving the original method unpatched at small client counts.
- Added independent switches, strict official assembly fingerprints, known-runtime blocking, target/accessor conflict checks and a late-conflict monitor.
- Added stock fallback paths, module status/counters, disposal checks and monitor-start rollback.
- Added differential official-method, callback/reentrancy, malformed-stream and concurrency tests.
- Added component timing/allocation comparisons and analysis tooling for supplied Lithos Probe captures.
- Kept AI, physics, tracking rules, world generation, packet scheduling and save behavior unchanged.
- No authenticated multiplayer capacity or NOS-versus-Stratum performance claim.

# 0.1.0 engineering preview

This starts a new, buildable Newfie's Server Optimizer source project for the
official Vintage Story 1.22.7 server. The earlier NOS source was not recovered.

- Added bounded tick processing-time distributions and effective TPS based on
  actual completed ticks. Engine sleep time is kept separate from processing.
- Added sampled process CPU, memory, allocations, GC collections/pause deltas,
  actual playing-session counts, entity/chunk counts and network payload totals.
- Added optional sampled system, phase and registered-callback timing. These
  measurements are inclusive and can overlap; unavailable worker costs remain
  explicit.
- Added independent instrumentation controls, conservative defaults, runtime
  version checks, patch-conflict diagnostics and hook cleanup on stop/dispose.
- Added administrator commands, bounded history, export retention and atomic
  JSON/CSV files with `latest` aliases.
- Added opt-in, bounded real-session evidence for authorized test servers.
- Added the separate official-client X11 input harness, seeded workload patterns,
  connection/action evidence accounting and load-generator resource reporting.
- Added controlled A/B/C/D campaign tooling, prepared-clone checks, randomized
  repeated trials, benchmark reports and a separate localhost dashboard.
- Added source, build/package scripts, configuration, engine inspection notes,
  regression tests and explicit validation boundaries.

No authenticated clients were available in this workspace. No player-capacity
improvement, multiplayer speedup, full production-modpack compatibility or
completed gameplay-correctness validation is claimed. Refer to the validation
report for actual runs and failures corrected during development.

AI activation/scheduling, entity repulsion, chunk scheduling/I/O, packet
coalescing, world-generation changes and incremental autosave are deferred
until a representative workload demonstrates a bottleneck and a minimal change
passes correctness and repeated performance comparisons.
