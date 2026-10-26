# Follow-up Claude branch audit — 2026-10-26

Baseline main: `bd529cf`. Reviewed new remote commits: optimizer `b00d8c3`; webmap branch disguise additions `3d906eb`, `4e1e8d5`, `abf7d5b`, `6891ea3`. Sailing remains excluded by instruction. Suite 1.2.53 / NOS 0.20.15 remain the current unreleased candidates from the preceding pass; this follow-up has Suite build suffix `ms11-claudeaudit3`.

## Accepted, with corrections

The opt-in 100 ms block-update diagnostic can expose queue pressure behind an otherwise opaque native callback. Default off, slow threshold 50 ms (10–1000), maximum one warning every 10 seconds. Counters record passes, slow passes and observer failures. It changes no queue draining, block updates, lighting, packet scheduling or save behavior. The native neighbour-notification queue already has a 500-entry limit; it is inaccurate to describe every queue as unbounded.

Corrections to the proposed change:

- The proposal labelled total minus changed-block time as block-entity sync. That residual can include hook overhead, scheduling pauses and unobserved work; if the changed-block child hook was blocked, almost the whole pass could be falsely assigned to sync. Added a separate sync observer, explicit unavailable states and remaining/unmeasured time. Elapsed durations do not prove a mod or GC caused a stall.
- Queue reads in an observational prefix could throw before native work. They now fail closed for the observer while allowing native execution.
- Observation/formatting/logging exceptions cannot replace native exceptions. Failures are counted and disable the observer until reapplied.
- Added failure monitoring for all three observational entries. Nested pass state is restored and child durations are scoped to their pass.
- Suite controls retain support detection, read-only/capture locks and opt-in defaults. Native and browser tests cover the additions; the server fixture enables all three hooks, observes actual callbacks, then disables them.

## Withheld: disguise command

**P1: ownership race in the delayed cleanup.** `OnEntitySpawn` runs before `Enable` or `Follow` installs the newly spawned entity's ID. It therefore schedules a 500 ms removal for the legitimate new entity. The delayed callback invokes `RemoveEntity` unconditionally; it never rechecks ownership. Normal disguises would be repeatedly removed/respawned and eventually disabled by the respawn limit. Reproduction compiles the exact proposed `DisguiseService` against official API types with stubbed admin/pose helpers, installs ownership after the spawn event, executes the captured callback, and observes the removal lookup despite ownership. No real world or player is used.

**P2: dimension is read but never applied.** Enable reads the player's dimension and ignores it; Follow discards it too. The used `TrySetPose` helper writes coordinates and rotation without dimension. This does not establish a correct follower position for admins outside dimension zero or crossing dimensions.

Other lifecycle/transaction concerns need a native entity test: hide/ghost failures after spawning, disposal restoring disguise-owned ghost state, callbacks after disposal, and cleanup of saved entities on later chunk loads. The supplied pure rules fixture does not exercise these paths. No disguise source, command registration or admin-service integration enters main.

An early suspicion that Entity.SidedProperties/AnimManager were fields was disproved by reflection: both are properties in the official 1.22.7 API. That is not an audit finding.

## Validation scope

Official Vintage Story 1.22.7 / .NET 10. Full optimizer driver includes 37 suites. The diagnostic fixture has 17 checks, including direct sync timing, unavailable child timing, rate limiting, untouched queues, native exceptions, throwing logger and failing diagnostic queue read. Browser tests cover controls/drafts/read-only modes and preserve the map/performance regressions from the previous pass. Disposable all-seven-mod integration checks live diagnostics activation, callback counters, disable, profiler lifecycle, explicit save, clean shutdown and copied-save integrity. These are correctness/observational checks, not a production performance improvement claim.
