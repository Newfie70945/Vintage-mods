# Implementation status: 0.2.0

Implemented real API patches: online player snapshot allocation reduction, bounded temporary-list reuse for nearby player queries and bulk FastMemoryStream reads. Each has its own switch, runtime state, conflict detection and fallback/disposal path. Profiler exports include module activation and replacement/fallback counters.

The optimizer remains a normal server-only mod for the official 1.22.7 runtime. It does not replace the server executable or permanently rewrite DLLs. No player-scaling, world-generation, AI-budget or whole-server throughput result is claimed from component microbenchmarks.

The previous 0.1.0 delivery contained profiling and testing tools only. Older research documents in this directory can describe that historical scope; this file and README describe the current release.

Deferred: tracking spatial indexes, AI/pathfinding scheduling, repulsion changes, chunk backpressure, packet batching, world-generation replacement, incremental autosave and chunk I/O rewrites. These need representative official-server profiles and gameplay/equivalence scenarios before implementation.

Original lost optimizer versions are not restored. This continues the reconstructed 0.1.0 project and validates recovered candidate code rather than assuming old working files were complete.
