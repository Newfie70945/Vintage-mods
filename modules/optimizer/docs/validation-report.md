# Validation report — 0.1.0 engineering preview

Validated on 17 October 2026 against the official **Vintage Story 1.22.7** Linux
server, .NET runtime **10.0.13**, using SDK **10.0.402**.

**Authenticated multiplayer clients tested: 0. No multiplayer speedup or increased
player-capacity claim is supported. No engine gameplay optimization is enabled.**

The server runs were short, isolated operational checks on a generated disposable
world. This was not the production world or a representative player workload.
The development host was shared with build/tool activity. These runs are not a
matched, repeated A/B/C/D performance campaign or a long-session memory test.

## Automated checks

All **60 checks passed**: 4 exact-engine metadata checks, 15 profiler regression
checks, 23 stress-tool checks, 14 benchmark checks and 4 dashboard checks.
The build completed with zero warnings and zero errors.

`artifacts/validation-build.log` in the complete download records the execution.
Use `python3 tools/build.py --game-dir /official/1.22.7 --dotnet dotnet` to rebuild
and rerun the suites. Source builds require the official game binaries separately.

Checks cover bounded history and method storage, percentile arithmetic, zero
steady-state allocation in the histogram insertion fixture, export retention,
nullable missing measurements, opt-in evidence bounds/authentication flags,
input cleanup and evidence expiry, benchmark identity and correctness gates,
console acknowledgements, and dashboard path/Host restrictions. The zero-byte
histogram result is a focused fixture result, not zero allocation for the mod or
server. The engine tests resolve exact target metadata without creating players.

## Actual dedicated-server checks

| Run | Timed idle window | Result |
| --- | --- | --- |
| Stock official server | 60 seconds after warmup | Startup, save, normal shutdown and SQLite integrity passed |
| Final mod, detailed hooks enabled | 60.376 seconds | 1,806 actual ticks, 58 samples, 59 timing entries; all three optional groups captured and removed |
| Final mod + public Suite 1.2.43 | 60.073 seconds | 1,809 actual ticks, 58 samples; optimizer-saved world reloaded, both mods loaded, capture/export/save/shutdown passed |
| Final mod with Enabled=false | 30 seconds after warmup | Explicit capture attempt rejected; no capture export; normal save and shutdown |

All final mod cases used assembly SHA-256
`d71ae7940e50bd9a30f4785b47acae8f3b4ba755a3a04dbc9dff51b2415d863d`.
The public Suite archive SHA-256 was
`e0e784ff90d594013aa2a03add261eb15480ed2cabd30d0ec7ad3ab0e0732b9f`.
Its web listener was disabled for this local check. Suite gameplay, web features,
anti-cheat consequences and OreShield behavior were not exercised.

The world was saved by the final optimizer build, then reopened in the Suite
coexistence run. Each final save passed `PRAGMA integrity_check`; persisted
counts were 1,800 chunks, 225 mapchunks, 16 mapregions and zero player records.
This establishes readable idle save/reload behavior, not correct real inventory,
entity, claim or combat persistence under gameplay load.

Real JSON/CSV data and summaries are in `benchmarks/validation/`. External
per-process samples include CPU seconds and working set; profile samples include
managed/committed/fragmented/private memory, process CPU, optional thread CPU,
GC and network counters. Default and detailed captures also passed report import
and localhost dashboard HTTP checks. A standalone HTML report is included.

The detailed capture had 57 timing entries with sampled calls. Empty method counters remain
explicit. Individual AI worker, pathfinding worker, chunk I/O, full asynchronous
save, per-player bandwidth and correctness measurements remain unavailable.

Tick work uses integer-millisecond engine samples: zero P95/P99 in an idle run
means the observed durations fell below that resolution. It does not mean the
server performs no work. Captures may include hook installation and first-use
JIT effects. Self-overhead counters exclude Harmony dispatch and other observer
effects; total profiler overhead has not been established by matched workloads.
No between-run improvement percentages are inferred here.

## Development failures found and corrected

- A preliminary capture did not start because the official console reader merged
  consecutive piped lines. The runner now waits for an acknowledgement of each
  command, and invalid captures fail validation.
- An optional detailed capture reached client-only dependencies while enumerating
  an entire engine assembly. Discovery now uses instantiated server systems,
  target signatures are checked independently, and optional groups isolate
  failures. Four new engine checks and the successful final detailed run cover
  this correction.

`benchmarks/validation/development-findings.json` preserves those findings. The
older default-profile adapter example is labelled separately; its assembly
predates the final detailed-hook correction and is not substituted for the final
build results above.

## Stress-tool and benchmark boundaries

Eleven scenario patterns were exercised through the CLI in dry-run mode. They
produced **zero connected players and zero confirmed game actions**. These are
scheduler/tooling checks, not game performance measurements.

The official-client driver requires separately installed, authenticated official
clients on Linux X11, licensed accounts, calibrated controls, prepared scenes
and trusted server evidence. It does not automate account login. Real GUI
execution has not been validated here. No stock baseline A session parser or
inventory/combat outcome observer is included. Presence alone does not establish
correct actions. The report generator blocks comparisons without matching real
work, authenticated sessions, timing and correctness evidence.

No 1–200-player workload, 100–1,000-mob scenario, heavily modded production
workload, 2–6-hour endurance test, Windows runtime check or total overhead
comparison was completed. See `limitations.md` and `client-feasibility.md`.

Following the supplied implementation order, AI scheduling, activation ranges,
repulsion, chunk/network coalescing, world-generation changes, incremental
saves and other engine changes remain deferred until representative profiling
and repeatable correctness/performance evidence justify a minimal change.
