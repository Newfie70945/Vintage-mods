# Suite 1.2.49 — cull and profiler allocation pass

Build ms11-cull-perf1. NOS remains 0.19.0-preview; previous anti-cheat fixes and NOS controls are retained.

Manual cull and preview now take a local player-position snapshot and reuse computed nearest distances between protection checks and ordering. They avoid repeated online-player enumeration and position resolution for every animal. Positions/distances are not cached across operations or ticks. Before destructive removal, current protection rules and player proximity are checked again; snapshots refresh after successful removal hooks. Existing rule limits, ordering policy, exclusions and generation protections remain. The manual cull is still synchronous: this pass does not promise a hard time budget or asynchronous cleanup.

Profile finalization ranks raw accumulators before creating hotspot, mod and phase output objects. It constructs only the retained top 200/100/120 rows instead of projecting every entry before discarding most of them. Stable tie ordering, retained row counts, mod aggregation and output values remain unchanged. Full path attribution still runs and may remain costly; this does not claim the entire measured finalization delay is eliminated.

New Suite timing rows: Animal Limiter manual cull; Animal Limiter manual preview; Server Health profile finalization; Server Health save correlation. Timings are wall time and can include waits/GC. No forced GC, altered save policy, skipped native block tick or modified fluid propagation is introduced.

The supplied capture identifies a separate autosave tick: 895.68 ms in The BASICs chat-history save callback, 616.80 ms in the native save stage, 121.36 ms in weather saving and 45.46 ms in Suite Admin saving. The BASICs is an external mod; its code/configuration is unchanged. Native finite-liquid delayed callbacks also account for 147.50 ms across 10,523 calls in another tick. These are measured callback intervals, not pure CPU attribution or proof of the cause of every simulation spike.

The delayed ServerHealth callback name in the provided log maps to StartProfile's completion callback in the previous local Suite assembly. The new profile-finalization timing will help quantify remaining work. No production before/after latency or TPS improvement is claimed.

Validation: build has zero warnings/errors; module/source consistency checks pass; 4,015 geometry and stable top-N comparison cases pass. Copied official server with 25 mod ZIPs passes 101 integration checks including animal-rule generation settings, timeline/incident views, profiler capture/control and native terrain fixtures. These tests are not a representative authenticated-player workload, a mass-cull stress test, or a benchmark proving resolution of the long stalls.
