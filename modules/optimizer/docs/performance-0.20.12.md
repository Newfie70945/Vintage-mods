# NOS 0.20.12 / Suite 1.2.51 — entity lifecycle event dispatch

## Change

Two independent experimental switches, `EntityLoadedDispatchReuse` and `EntityDespawnDispatchReuse`, replace the official event dispatch's per-call invocation-array and capturing Action allocations with .NET's immutable delegate enumeration. Both default to false. Suite > Optimizations exposes separate switches and a combined execution counter.

Every subscriber still runs synchronously in its original order. Registration/removal during dispatch affects subsequent events, not the captured event. A failing subscriber is logged through the same logger with the same message and remaining subscribers continue. No callback, chunk, save, inventory, AI interval or population limit is skipped or changed.

The native tick profiler, when enabled on the actual server main thread, gains `nos-entity-event:<type>.<method>` scopes around individual subscribers. Weak delegate keys avoid retaining disconnected or removed subscribers just to label them. Profiling from another thread does not touch the main-thread profiler.

Each path validates the official event body and participates in the existing Harmony conflict monitor. Foreign patches to its trigger or shared generic dispatcher block activation. Infrastructure failures disable that path without swallowing its exception or replaying partially completed work. Subscriber exceptions retain native handling and do not trip this safeguard.

## Why this target

The supplied logs show expensive chunk completion and unloading, while candidate scans and block entity initialization account for little of the measured stall. Entity loading/unloading invokes these events. Native `despawned-*` labels can charge a global subscriber's time to a later animal label. This patch removes a small allocation source in that path and exposes subscriber work, but it cannot shorten a third-party callback that waits 150 ms or identify it before another capture is collected.

## Evidence and limits

The actual official 1.22.7 dispatch bodies were exercised with 0, 1 and 8 controlled subscribers. Snapshot mutation, subscriber exceptions/log continuation, recursive dispatch, empty dispatch and profiler thread separation were checked. Warm optimized dispatch allocated 0 bytes/event in these controlled cases; native dispatch allocated 88–184 bytes/event. Timings varied with JIT state and are not evidence of a production TPS gain.

A new production capture is still required to attribute the repeated unload waits. The separate The BASICs chat-history autosave handler remains outside this patch. This change does not promise elimination of the captured multi-second stalls.

## Collecting the next useful capture

In Suite > Optimizations, enable one of the new dispatch switches and save/apply. Confirm its module is active rather than conflicted. Use the existing native `/debug logticks` capture during the same kind of exploration/unloading that stalled before. Inspect the `nos-entity-event:` children within the slow tick: a named subscriber consuming most of the unload time is direct timing evidence; a fast subscriber scope means look elsewhere in the native unload path. Do not blame the next animal name in the old mark list. Turning the new switch back off restores native dispatch.

## New standard stress workload

At the owner’s request, the modpack laboratory defaults now use 6,000 real active entities, 2,000 barrels and 2,000 firepits, with 180-second measured windows. The fixture supports up to 8,192 entities and 4,000 of each block; these are lab bounds, not recommended production populations. The kept-loaded test area expands to 12 × 12 chunk columns so the population remains within prepared terrain. Tests check exact spawned/placed counts and validate that dirty/save coverage matches the generated columns and native world height. This is synthetic stress, not 6,000-player capacity evidence. Smaller explicit workloads remain available for targeted investigations.

## Completed standard comparison

One sequential off/on pair used the same official 1.22.7 world template, Suite 1.2.51, NOS 0.20.12, 23 selected mod ZIPs and fixture. Only the two new dispatch flags differed. Each run measured 180 seconds with 6,000 active animals (pig, sheep, goat, deer groups), 2,000 barrels and 2,000 firepits in 144 kept-loaded columns. No authenticated players were present; NOS capture and the full native profiler were stopped during measurement. No compilation ran during either measured window.

| Metric | New dispatch options off | On |
| --- | ---: | ---: |
| Active fixture entities at end | 6,000 | 6,000 |
| Recorded ticks | 960 | 960 |
| Average tick | 187.08 ms | 187.12 ms |
| p95 / p99 | 238 / 265 ms | 241 / 264 ms |
| Worst tick | 383 ms | 427 ms |
| Allocation rate | 77.43 MiB/s | 76.87 MiB/s |
| Allocation per recorded tick | 14.5236 MiB | 14.4210 MiB |
| End managed heap | 1,826.53 MiB | 1,816.90 MiB |
| Process CPU (100% = one core) | 112.80% | 112.64% |
| GC pause total in window | 3,924.70 ms | 3,864.93 ms |
| Median explicit save preparation | 78.59 ms | 70.16 ms |

Both runs completed five explicit saves, forced unloading, clean shutdown and SQLite integrity checks with zero logged errors. Save/unload and test-only forced collections were outside steady windows. The enabled run reported both modules active and zero dispatch infrastructure failures. The counter snapshot, taken before forced unloading, recorded 228 loaded-event dispatches and 110 despawn dispatches; it does not count the later forced-unload phase.

**Conclusion:** the controlled dispatch component eliminates its own 88–184 bytes/event, but this high-population pair demonstrates no meaningful steady-tick improvement. The small allocation/save differences are not attributable from one pair and must not be marketed as a performance gain. The worse candidate maximum is also insufficient to assign causality. Both new switches remain experimental and off. This two-core/eight-GiB test is synthetic stress evidence, not a production scaling or long-term leak claim. The smaller interrupted comparison was discarded.

[Controlled dispatch measurements](research/events-0.20.12/dispatch-component.json) · [Sanitized standard comparison](research/events-0.20.12/server-comparison.json).
