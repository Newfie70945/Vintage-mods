# Implementation status: 0.7.0 preview

NOS is a server-only mod for the official Vintage Story 1.22.7 server. It applies reversible Harmony patches in memory and does not replace the server runtime or permanently rewrite DLLs.

Implemented independent engine modules:

- Online player snapshot allocation reduction.
- Nearby-player temporary-list reuse, default off and population gated when enabled.
- Bulk FastMemoryStream reads.
- Bounded server-assets compression cache.
- AI execution-filter delegate enumeration without invocation arrays.
- AI task-start veto enumeration without invocation arrays or LINQ enumerators.
- Weakly cached repulsion callbacks and removal of the verified always-true range predicate.
- Allocation-free reuse of existing generation requests.
- Release of completed save snapshots so unloaded chunks are not retained until the next save.

Each has a separate switch and runtime status, exact official-assembly guards, contract/conflict checks, disposal and stock fallback. Suite controls use the versioned event bridge. Profiling can remain off while engine modules operate.

[Current validation](performance-0.6.0.md) separates component measurements from the full Suite/selected-modpack laboratory workload. Neither establishes production player capacity, complete mod compatibility or superiority to another runtime. Loaded modules are not equivalent to every feature being actively exercised.

Deferred: AI/pathfinding scheduling, activation ranges, chunk backpressure, network batching, incremental autosave, chunk I/O and terrain algorithm changes. These require representative profiles plus gameplay/equivalence validation before implementation.
