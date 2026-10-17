# NOS 0.2.0 validation report

Built and validated against the inspected official Vintage Story 1.22.7 server with .NET 10.0.402 SDK / .NET 10 runtime on Linux x64.

## Correctness and lifecycle

- Build: 0 errors, 0 warnings.
- 4 official engine metadata checks.
- 21 engine module lifecycle/conflict checks, including independent switches, master off, safe mode, population activation/deactivation, accessor conflicts, late foreign Harmony patches, load-tool/runtime name distinction, monitor-start rollback and own-only disposal.
- 31 player-query checks against actual official methods and injected prefixes: ordering, pending connections, output ownership/runtime type, range/dimension edge cases, predicate side effects, exception identity, dictionary mutation, reentrancy, concurrent separate threads and bounded/cleared scratch.
- 5,347 stream differential cases in each enabled/disabled state; 53,477 assertions including results, exception type/message, backing/destination contents and stream position. Independent-stream concurrency and zero steady-state copy allocation were checked.
- 15 profiler regression checks.
- Python tooling: 23 stress-tool, 14 benchmark, 4 dashboard and 4 probe-analysis tests passed. Synthetic tool fixtures are not server workload evidence.

## Component performance

Five repeated stock/prefix-disabled/candidate measurements were made against the real official methods with uninitialised test fixtures. Raw data and medians are in `research/components-0.2.0/` and `performance-0.2.0.md`.

Online snapshots reduced allocation and method cost in measured cases. Bulk stream reads replaced the official byte loop with a measured faster bulk copy on eligible buffers. Nearby-query temporary-list reuse reduced allocation but had mixed CPU results, so it is **OFF by default** and population-gated if enabled. No extrapolation to server TPS or player capacity is justified.

## Dedicated-server checks

Final default-active, master-disabled and Suite 1.2.44 co-load checks used a disposable stopped world on the official dedicated server. Each reached ready state, completed the selected command/profile checks, saved and exited with code 0. SQLite world-save integrity was `ok`, with no server errors. Exported module state verified the default snapshot and stream modules as active and the experimental nearby module as disabled. Master-disabled capture was rejected as expected.

The active idle capture recorded real online-snapshot replacement calls. The bulk-copy path was not invoked by that idle world; its correctness/performance evidence comes from the differential tests and component measurements, not an invented idle traffic workload.

Summaries are in `research/server-smoke-summary-0.2.0.json`. These were approximately 30-second **functional smoke checks**, with no authenticated clients. They do not establish multiplayer performance, long-session stability, retained-memory improvements, graphical behavior or complete modpack compatibility.

## External workload

Only `lithosprobe-20260930-125523.json.gz` was supplied. It contains StratumServer frames and a 2,000-player endpoint; it is not a vanilla baseline or NOS trial. Analysis and sample/window caveats are in `research/supplied-probe.md`. The raw uploaded capture is not redistributed.

## Next validation boundary

Run staging comparisons on a representative world/hardware with actual authorised client activity or a validated load tester. Preserve workload phases and correctness evidence; compare MSPT P95/P99, CPU, allocation, GC pause, post-GC heap floor, queues and save integrity across repeated stock, module-disabled and one-module trials before enabling a combined production set.
