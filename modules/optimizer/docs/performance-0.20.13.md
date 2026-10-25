# NOS 0.20.13 / Suite 1.2.51 — callback registration and despawn cleanup

`ListenerRegistrationIndex` is an independent experimental switch, off by default. It indexes first-empty callback slots for the official entity/block tick-listener registration methods. Native listener construction, ID generation, slot writes, dictionary updates and registration order remain in the native method. A version/count change invalidates the index, so ordinary list changes, removals and defragmentation trigger a rebuild. Off-thread, subclassed and over-limit lists retain the native search. The index is bounded to 65,536 slots and weakly keyed by the listener list; it retains no listener/entity objects.

The index assumes mutations use ordinary List APIs, which update the native list version. Mods that modify a listener list's backing array directly without updating its version are outside this contract. Leave this experimental option off with such mods. Foreign patches on the target or skipped List accessors prevent activation; a native exception propagates without replaying a partial registration.

`DespawnCleanupTimings` is optional diagnostics, off by default. During native tick profiling on the server main thread, it labels individual behavior cleanup and animation disposal. It does not shorten cleanup or skip/retime callbacks. A cleanup exception retains its original propagation and disables the extra diagnostic wrappers for subsequent calls. The observed failure counter does not establish that NOS caused the original exception.

The 6,000-entity stress profile placed about 59% of exclusive captured time in physics-related scopes and showed repeated catch-up of 8–10 steps. The template explicitly selected one native physics worker. A native-worker settings comparison must be labelled separately from optimizer patch results; changing the worker count is not evidence of a NOS code speedup.

The standard remains 6,000 active animals, 2,000 barrels, 2,000 firepits, four animal groups and 180-second steady windows. Explicit lab settings overrides and optional unload profiling are recorded. Unload profiling adds instrumentation overhead and runs outside steady measurement.

## Validation and component performance

All 34 automated suites passed against official 1.22.7, including 152 engine lifecycle/conflict checks, 24 callback-registration checks and 10 actual native despawn-cleanup checks. Final optimizer, Suite and lab fixture builds completed with zero warnings/errors. Browser control tests passed save/apply, older-version omission, safe/read-only modes and mobile layout.

In three alternating component trials registering 6,000 callbacks through the actual native methods, median entity registration time fell from 11.86 to 2.68 ms; median block registration fell from 12.60 to 2.49 ms. These are controlled native registration timings with synthetic timestamps, not whole-tick speedups. Allocation remained similar: native listener objects still have independent ownership. Cold/JIT trial results are retained in the raw data. Tests cover first-hole ordering, listener IDs/index dictionaries, normal list mutations, off-thread fallback and collection of old list keys.

The mass-unload test exercised approximately 100,000 timed cleanup calls in each arm with zero observed cleanup failures. Behavior callbacks and animation disposal have separate native profiler scopes. This selected pack did not reproduce the public server's repeated 150 ms per-entity waits; the new scopes provide evidence for a fresh live capture rather than claiming those waits are fixed.

## Native worker setting comparison — separate from patch claims

Both runs used exactly the same NOS 0.20.13 and Suite 1.2.51 binaries/configuration, with the new index and cleanup diagnostics enabled. Only native `MaxPhysicsThreads` changed. Each measured 180 seconds with 6,000 active animals, 2,000 barrels, 2,000 firepits and 23 selected mod ZIPs on the same 2-vCPU / 8-GiB lab. Actual runtime worker counts were recorded, not inferred from the configuration. Neither NOS capture nor native profiling ran during the steady window.

| Metric | One native worker | Two native workers |
| --- | ---: | ---: |
| Active fixture entities at end | 6,000 | 6,000 |
| Recorded ticks | 865 | 1,766 |
| Average tick | 207.76 ms | 101.49 ms |
| p95 / p99 | 272 / 307 ms | 134 / 171 ms |
| Worst tick | 393 ms | 353 ms |
| Process CPU (100% = one core) | 112.16% | 154.92% |
| Allocation rate | 75.64 MiB/s | 91.37 MiB/s |
| Allocation per recorded tick | 15.750 MiB | 9.319 MiB |
| End managed heap | 1,823.2 MiB | 1,845.0 MiB |
| GC pause total in window | 3,938.70 ms | 4,812.38 ms |
| Median explicit save preparation | 66.42 ms | 68.84 ms |
| Instrumented unload observer wall time | 281.57 ms | 204.41 ms |

Both passed five save cycles, forced unloading, clean shutdown and database integrity with zero logged errors. The callback index skipped approximately 91 million leading slot iterations in each complete run, with 53 rebuilds and zero registration failures/fallbacks. Saves, full native profiling, instrumented unloading and test-only full GCs occurred outside steady timing. Unload observer time includes queue/instrumentation overhead and is not isolated cleanup CPU time.

The native setting comparison reduced average ticks by about 51% in this pair by using more available CPU; it is **not a NOS patch speedup**. Allocation per second and GC pause total rose while more than twice as many ticks completed. The workload still averaged over 100 ms per tick and does not demonstrate 30 TPS at this population. One sequential pair is not a universal tuning recommendation or production capacity claim. No public-server native settings were changed.

## Trying the changes

Suite > Optimizations > API and allocation modules exposes **Index free callback slots during chunk/entity loading (experimental)**. Save/apply, then verify both registration module states are active. It defaults off; existing settings are preserved. Restore native registration by switching it off.

For a short investigation, enable **Despawn cleanup timings**, then use the native tick profiler / logticks during the problematic unload. Look for `nos-despawn-behavior:<type>` and `nos-despawn-animation-dispose`, alongside the earlier `nos-entity-event:<type>.<method>` subscriber scopes. Native failures still propagate once; timing does not repair a failing cleanup handler. Profiling itself adds overhead, so these instrumented captures must not be compared with profiler-off tick latency.

For future runs on this two-core lab, explicitly record `--physics-threads 2`; retain a one-worker reference only for a labelled settings comparison. The harness leaves the template's worker setting unchanged when no override is requested. Actual public-server tuning must account for its hardware and current worker count.

[Registration component data](research/listeners-0.20.13/registration-component.json) · [Native worker comparison](research/listeners-0.20.13/native-worker-comparison.json) · [Heavy diagnostic capture](research/listeners-0.20.13/heavy-profile-summary.json).
