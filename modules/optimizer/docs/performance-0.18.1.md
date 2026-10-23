# NOS 0.18.1 — allocation defaults and barrel hot path

The newest supplied capture records 222 mods, 22 players, 2,987 entities and 51,456 loaded chunks at export. Its completed three-second automatic profile has 25 ticks: average 43.53 ms, P95 67.40 ms, maximum 89.34 ms. Entity animation contributes 17.41% and barrel callbacks 3.51%. This was a follow-up to a 1,611.85 ms scheduler delay, not a capture of that stall. Lower entity population and different workloads prohibit interpreting it as an optimizer before/after trial.

## Changes

- Enable animation dictionary scanning, native barrel pairing and berry callback flattening by default when a configuration field is absent. Existing explicit opt-outs remain disabled. Every module remains independently switchable and subject to compatibility/failure gates.
- Tighten barrel ingredient validation to a bounded direct scan of at most two native ingredients, retaining mod ingredient fallback. Check the disabled gate before inspecting recipe data. Pairing result ownership, match order, custom collectible hooks and fermentation remain unchanged.
- Correct the stale human-readable optimizer version: status now uses the compiled assembly version, matching the manifest and structured bridge.
- Suite stays at 1.2.49; its existing controls support these settings. No automatic overwrite of production configs occurs.

## Selection and limits

The prior component evidence measured animation fixture allocations at 176 -> 120 bytes/call, two-input barrel pairing at 384 -> 88 and berry setup with a behavior at 568 -> 472. These are per-fixture allocations, not predicted server-wide memory or TPS savings. Berry setup is not the expensive climate catch-up body. Animation work still performs native frames, transforms, collision assignments and triggers. Runtime type checks and foreign Harmony guards stay enabled. Native empty-firepit bypass, unload staging and scheduling policies remain opt-in; no forced collections, save deferral or altered AI cadence is introduced.

The newest export also retains a 17,098 ms scheduler delay with only 134 ms measured GC pause and no recorded save overlap. A short profile taken afterward cannot explain that stall. Low whole-process CPU alone does not exclude a blocked game thread. Allocation reductions are worthwhile, but do not prove resolution of the large stalls.

Raw uploaded diagnostics remain private and are not included in source or release packages.

## Validation

All 28 optimizer test suites pass against official 1.22.7 / .NET 10, including 89 block callback checks, 54 collision/animation checks and 115 module lifecycle/conflict checks. Added configuration assertions verify the promoted defaults, explicit opt-out preservation and unchanged opt-in policies. Build: zero warnings/errors.

Additional sequential trials with tiered compilation disabled reproduce barrel fixture allocations of 384 -> 88 bytes, berry behavior setup 568 -> 472, empty bush setup 32 -> 0, and animation fixture allocations 176 -> 120. CPU samples remain host-sensitive and are not a production claim. [Block measurements](research/defaults-0.18.1/block-components.json), [animation measurements](research/defaults-0.18.1/animation-components.json).

Copied official server with 25 mod ZIPs: 91 integration checks pass, including actual default activation through the authenticated Suite bridge, independent switches, persistence, guarded ARL firepit conflict and native terrain/entity fixtures. No authenticated-player workload or production TPS claim. [Sanitized integration report](research/defaults-0.18.1/copied-server.json).
