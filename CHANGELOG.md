# Current release changes

## Server Essentials Suite 1.2.46 + Server Optimizer 0.3.1 preview

- Rejects compression work started before a cache reload or budget change, preventing stale work from repopulating a new cache.
- Applies a lowered cache limit immediately, even when the server-assets buffer stays the same.
- Tracks new failures per module activation, so historical failure counters do not permanently break retries.
- Removes owned patches even if compatibility-listener unregister fails; stranded standby states now report monitor failure.
- Caches resolved patch targets/dependencies and samples cache counters once per diagnostic read, reducing repeated monitor work.
- Expanded regression coverage: 500 cache reload cycles, actual array release, budget changes, late completion and failure/retry/teardown cases.


- Added NOS controls to the Suite's Optimizations page: master switch, safe mode, independent engine modules, population threshold and bounded asset-cache size.
- Shows actual activation states, conflict/fallback reasons and execution counters separately from saved settings.
- Saves optimizer.json and reapplies engine modules without restarting. Active NOS captures lock settings; Read Only accounts and Suite Safe Diagnostics Mode restrict changes.
- Added NOS capture start/stop and report export from the Suite.
- Added an independently guarded server-assets compression cache. Unchanged asset bytes can reuse compressed output; packet delivery stays in the official engine. Each send gets separate bytes, asset changes invalidate reuse, and retained source/compressed bytes are capped.
- Retains the player-count timeline, 1m–1h ranges (30m default), marker coverage explanation and sampled slow-tick/recurring-cause analysis from 1.2.45.
- Removed obsolete per-module changelog files. Older changes remain in Git history.

Companion module versions are unchanged. NOS is an official-server mod, not a replacement runtime. Component and idle-server validation do not establish a production TPS improvement.
