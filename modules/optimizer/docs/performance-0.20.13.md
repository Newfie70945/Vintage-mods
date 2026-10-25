# NOS 0.20.13 / Suite 1.2.51 — callback registration and despawn cleanup

`ListenerRegistrationIndex` is an independent experimental switch, off by default. It indexes first-empty callback slots for the official entity/block tick-listener registration methods. Native listener construction, ID generation, slot writes, dictionary updates and registration order remain in the native method. A version/count change invalidates the index, so ordinary list changes, removals and defragmentation trigger a rebuild. Off-thread, subclassed and over-limit lists retain the native search. The index is bounded to 65,536 slots and weakly keyed by the listener list; it retains no listener/entity objects.

The index assumes mutations use ordinary List APIs, which update the native list version. Mods that modify a listener list's backing array directly without updating its version are outside this contract. Leave this experimental option off with such mods. Foreign patches on the target or skipped List accessors prevent activation; a native exception propagates without replaying a partial registration.

`DespawnCleanupTimings` is optional diagnostics, off by default. During native tick profiling on the server main thread, it labels individual behavior cleanup and animation disposal. It does not shorten cleanup or skip/retime callbacks. A cleanup exception retains its original propagation and disables the extra diagnostic wrappers for subsequent calls. The observed failure counter does not establish that NOS caused the original exception.

The 6,000-entity stress profile placed about 59% of exclusive captured time in physics-related scopes and showed repeated catch-up of 8–10 steps. The template explicitly selected one native physics worker. A native-worker settings comparison must be labelled separately from optimizer patch results; changing the worker count is not evidence of a NOS code speedup.

The standard remains 6,000 active animals, 2,000 barrels, 2,000 firepits, four animal groups and 180-second steady windows. Explicit lab settings overrides and optional unload profiling are recorded. Unload profiling adds instrumentation overhead and runs outside steady measurement.
