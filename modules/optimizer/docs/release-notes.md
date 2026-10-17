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
