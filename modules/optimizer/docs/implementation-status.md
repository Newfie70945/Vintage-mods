# Implementation status: 0.15.0 preview

NOS is a server-only mod for the official Vintage Story 1.22.7 server. It applies reversible Harmony patches in memory and does not replace the server runtime or permanently rewrite DLLs.

Implemented independent engine modules:

- Despawn packet projection without three temporary LINQ iterators.

- Bounded physics packet temporary-list reuse with unchanged packet arrays and send calls.

- Nearest/interactable entity query state reuse without per-query closures.
- Worker path-queue cap repair, retaining requests that previously went missing.
- Optional bounded main-thread path assistance, off by default.
- Explicit release of stopped capture references without forced GC.
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

[Current validation](performance-0.15.0.md) separates component measurements from the full Suite/selected-modpack laboratory workload. Neither establishes production player capacity, complete mod compatibility or superiority to another runtime. Loaded modules are not equivalent to every feature being actively exercised.

Deferred: full entity activation/sleep, broad pathfinder algorithm/cache replacements, activation ranges, chunk backpressure, network batching, incremental autosave, chunk I/O and terrain algorithm changes. These require representative profiles plus gameplay/equivalence validation before implementation.

See [replacement coverage](replacement-coverage.md) before removing an external optimizer. Feature goals do not establish equivalent behavior or measured performance.

0.10.0 adds five independently controlled opt-in modules: distant AI evaluation, wander checks, stationary repulsion, waypoint candidate-node pooling and missing-attribute resync suppression. Full entity activation/sleep, TCP consolidation and paired client protocols remain unimplemented. See replacement-coverage.md and performance-0.10.0.md.

0.11.0 adds optional empty-exclusion broadcast fast paths and bounded local entity cleanup/despawn lists. The complete original broadcast send bodies and full entity ticking remain. See performance-0.11.0.md for validation and limits.

0.12.0 adds independently controlled block-update list reuse, chunk-request staging/coordinate reuse and unload candidate/notification scratch reuse. No random-tick reduction, chunk delivery cap, I/O rewrite or autosave change. See [validation](performance-0.12.0.md).

0.13.0 adds normal/debug callback key snapshots, private spawn-opportunity scratch and exact-length spawn chunk-read arrays, all independently controlled with bounded retention. Shared compatibility inspections are deduplicated within each monitor pass. [Validation](performance-0.13.0.md).

0.14.0 adds four independent opt-in modules: nearby/cuboid entity query scratch and base shapeless merge/wildcard allocation reductions. Returned arrays and item clones remain independently owned; crafting hooks and native query traversal remain. [Validation](performance-0.14.0.md).

0.15.0 adds independently controlled method-local struct traversal for native collision, InWorldContainer transition scans and InWorldContainer ticks. It preserves custom inventory enumerators and adds no stationary collision bypass. [Validation](performance-0.15.0.md).

## Collision generation and animation (0.16.0)

Three independent controls default off: `CollisionGenerationReuse`, `CachedCollisionGenerationReuse` and `AnimationCollisionFlagScan`. Collision callbacks reuse cleared thread-local context only with the verified synchronous official walker; custom walkers retain native allocation and ownership. Animation scans use a struct dictionary enumerator without skipping frames, poses or triggers. Collision fixtures allocate less but show mixed CPU timings, including slower warm cache hits. See [evidence and limits](docs/performance-0.16.0.md).
