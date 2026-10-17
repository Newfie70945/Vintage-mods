# Suite 1.2.45 — Performance investigation

- Added online player count to the Unified Performance Timeline.
- Added 1-minute and 1-hour analysis windows alongside 5, 15 and 30 minutes; 30 minutes remains the default. The timeline has its own range-cycle button linked to the page's window selector.
- Retains up to one hour of in-memory, one-second health samples. History starts when diagnostics are enabled and is not backfilled or persisted across restarts.
- Tick Debugging now displays the 60 worst sampled ticks from the latest completed engine capture, with timestamps and leading profiler work.
- Counts the leading cause of every sampled tick of at least 50ms during that capture, independently of worst-tick retention. Common-cause rows include marker-based likely owner and confidence. The cause catalog is bounded to 256 distinct markers plus an overflow group.
- Expanded profiler ownership matching to concrete handler types from mod assemblies, including handlers outside their main ModSystem namespace. Scanning is bounded to 256 types per assembly; ambiguous ownership stays unresolved.
- Added marker ownership coverage and an explanation of unresolved engine/shared work. Matching a marker does not prove a mod defect.

Captures sample the engine profiler; these results do not account for every server tick. The feature does not enable continuous heavyweight profiling. Existing native slow-tick log capture remains available.
