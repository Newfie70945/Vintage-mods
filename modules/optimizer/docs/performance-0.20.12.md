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
