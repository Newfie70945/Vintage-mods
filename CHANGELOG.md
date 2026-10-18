# Current release changes

## Suite 1.2.48 + NOS 0.5.0 preview

- Removed per-lookup closure allocations from generation request handling: 96 bytes to zero for repeated requests in the official-method fixture, while preserving request order, pass escalation and generation priority.
- Fixed background-save snapshot retention. Completed saves release their chunk references inside the stock save lock, allowing otherwise-unreferenced unloaded chunks to be collected. Failed saves retain their retry snapshot.
- Added independent switches for both modules in Suite → Optimizations, enabled by default and usable with profiling stopped.
- Added guarded activation, dependency conflict checks, live stand-down and original-method fallback for both modules.
- Retains the prior AI, repulsion, asset compression, stream and player-query optimizations.
- Companion module versions are unchanged. One current changelog remains; historical notes are in Git history.

Correctness tests, component measurements and running-server save validation are documented in modules/optimizer/docs/performance-0.5.0.md. These changes do not establish a production TPS increase or player-capacity figure.
