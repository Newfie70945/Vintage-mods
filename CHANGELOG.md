# Current release changes

## Suite 1.2.47 + NOS 0.4.0 preview

- Added an allocation-free AI filter dispatch module. All veto callbacks retain their original order, exception behavior and decision results; AI schedules and tick rates stay unchanged.
- Added a repulsion hot-path module: reuses the walk callback and eliminates an exactly verified always-true range predicate, saving two delegate allocations per eligible entity tick and redundant per-neighbor calls.
- Both modules work with profiling stopped, default on, and have independent switches in Suite → Optimizations.
- Added exact Essentials assembly verification and conflict checks for affected AI/repulsion methods.
- Suite controls handle older NOS modules without sending unsupported new settings.
- Retains prior bounded asset compression, player-query and stream optimizations, reload/memory-bound fixes, failure retries and shutdown cleanup.
- Companion module versions are unchanged. One current changelog remains; historical notes are in Git history.

Component measurements and correctness boundaries are documented in modules/optimizer/docs/performance-0.4.0.md. No production TPS improvement or player-capacity claim is made.
