# Disposable generation/save A/B performance check

Build `tests/StorageBenchmarkProbe` against the official 1.22.7 API. This test-only DLL queues a fixed generation workload, dirties 512 chunks per save, tracks weak references through explicit unloads, and supplies boundary counters. It is never packaged in production mod ZIPs. Forced compacting GCs standardize laboratory live-heap comparisons outside the timed tick capture; they are not NOS features.

Run `tools/storage_performance_check.py --help`. Supply a clean stopped snapshot, official game directory, .NET executable, shipping Suite/NOS ZIPs, fixture DLL and a fresh output directory outside the snapshot/game. It binds only to loopback, keeps player authentication enabled and does not create clients or advertise publicly. Each run clones the same snapshot and does not modify the original. Default order is stock, NOS, NOS, stock, stock, NOS.

The stock arm keeps the same Suite, NOS observer and test fixture, with all NOS engine switches off. The NOS arm uses shipping default switches. Both get identical captures, 64 fresh terrain columns, five saves after marking all 512 target chunks dirty, unload and snapshot-refresh stages. Only the automatic-save interval changes from the template, to avoid an unscheduled save; explicit save operations are identical. This is a chunk/storage stress fixture without authenticated players, not a multiplayer or entity benchmark.

Each run has raw console logs, external process samples, complete profile telemetry, process-wide allocations/GC boundaries and SQLite integrity results. `analyze.py DIRECTORY --output FILE` derives per-run and median/range summaries. Save timings come from console-log observations; shared host/container storage, OS caches, only three repetitions per arm and integer-ms official tick measurements limit interpretation. Report mixed or overlapping results. Do not turn these values into production TPS or supported player counts.

The after-unload and after-snapshot-refresh measurements distinguish snapshot references from other engine caches. A retained object count is not equivalent to retained bytes or process RSS. The source and original benchmark save must be supplied by the server owner; proprietary binaries/saves are not distributed with this tool.

Use `--order storage,stock,storage` for a follow-up that enables only the two new generation/save switches. Natural entity spawning remains enabled; record its counts from each profile and do not attribute differing save/tick timings solely to NOS when those counts vary.
