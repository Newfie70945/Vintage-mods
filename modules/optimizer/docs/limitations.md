# Validation boundaries and rollback

This is an engineering preview, not a production performance release.

## Available evidence

- The mod is compiled against official Vintage Story **1.22.7**, .NET 10.
- Regression tests exercise bounded storage, measurement arithmetic, cleanup,
  configuration validation and tool behavior. See the delivered validation
  report for exact commands and results.
- Isolated local dedicated-server runs check actual loading, commands,
  profiling/export, shutdown and database integrity after save/reload.
- Short idle runs are not representative multiplayer performance tests.

## Evidence not available

- Authenticated official-client sessions: **0 actually tested** in this
  workspace. Client-account credentials and official graphical client binaries
  were not available.
- No real production-world clone or full production modpack was provided.
- xSkills, modded AI, claims, economy, shops, Discord, map integrations,
  Client Integrity, Anti-Cheat, OreShield and the public Suite's complete
  gameplay flows have not been validated together here.
- No 2–6 hour endurance run, 100–1,000 mob combat workload or 200-client test
  has been executed. These remain test targets, not supported-capacity claims.
- Save-file integrity after an idle reload does not prove block, entity,
  inventory, claim or transaction correctness under gameplay load.
- No measured engine optimization has been justified by a representative
  workload yet. No aspirational percentage from the supplied prompt is a result.

## Metric interpretation

Process CPU includes every server thread. It is not a measurement of main-thread
CPU. Working set, private memory, managed heap, GC committed memory and allocation
rate describe different things. A rising working set alone does not establish
a leak. Sampled task timing can miss rare calls and must not be interpreted as
complete CPU attribution. GC information reports what the runtime actually
exposes; unsupported pause/thread/network-attribution metrics must remain
explicitly unavailable rather than estimated as zero.

Processing-time percentiles and tick-spacing/TPS measurements have different
denominators. Instrumented-hook timings are inclusive when nested, and sums
can double-count work. The dashboard does not turn them into an exclusive
third-party-mod ranking. Startup, warmup and timed workload windows must remain
distinct in benchmark reports.

Starting a capture installs the selected observational hooks. Capture duration
and the first completed tick can include installation and first-use JIT costs;
the collector does not silently subtract those costs. Optional detailed hooks
can have a larger setup cost than the default tick observer. A short capture's
worst tick must not be advertised as steady-state gameplay cost or an optimizer
speedup. Plan and inspect warmup and measurement boundaries explicitly.

## Compatibility behavior

Instrumentation uses optional hooks only after checking the inspected target
version and existing patch ownership. Conflicts, missing targets and failures
must remain visible in status and exports. Disabling an unavailable measurement
must leave the engine's original behavior running. Ordinary player packets,
permissions, block concealment, world updates and save scheduling are unchanged.

Do not install this preview beside an older NOS build while collecting controlled
benchmarks; that introduces an unmeasured intervention. Test the chosen mod set
on a separate clone first. The public Suite 1.2.43 ZIP is not modified by this
project, and its existing engine/native dependencies are outside this mod.

## Rollback

Stop the test server normally. Remove the optimizer ZIP from `Mods`, preserve
exports if useful, and start the server again. This preview does not replace
official assemblies, change the world format or require an offline runtime
restore. Keep normal world backups and verify the unchanged game version.

For the client harness, stop its run; its cleanup releases held input keys.
For the dashboard, stop the separate Python process. No game-server firewall,
DNS or web-host configuration needs changing.
