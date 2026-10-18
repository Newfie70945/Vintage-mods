# Current release changes

## Server Essentials Suite 1.2.46 + Server Optimizer 0.3.0 preview

- Added NOS controls to the Suite's Optimizations page: master switch, safe mode, independent engine modules, population threshold and bounded asset-cache size.
- Shows actual activation states, conflict/fallback reasons and execution counters separately from saved settings.
- Saves optimizer.json and reapplies engine modules without restarting. Active NOS captures lock settings; Read Only accounts and Suite Safe Diagnostics Mode restrict changes.
- Added NOS capture start/stop and report export from the Suite.
- Added an independently guarded server-assets compression cache. Unchanged asset bytes can reuse compressed output; packet delivery stays in the official engine. Each send gets separate bytes, asset changes invalidate reuse, and retained source/compressed bytes are capped.
- Retains the player-count timeline, 1m–1h ranges (30m default), marker coverage explanation and sampled slow-tick/recurring-cause analysis from 1.2.45.
- Removed obsolete per-module changelog files. Older changes remain in Git history.

Companion module versions are unchanged. NOS is an official-server mod, not a replacement runtime. Component and idle-server validation do not establish a production TPS improvement.
