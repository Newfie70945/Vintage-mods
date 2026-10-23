# NOS 0.17.0-preview — diagnostic-driven corrections

Suite 1.2.48 `ms11-diagnostics-fix1` pairs with NOS 0.17.0-preview on official Vintage Story 1.22.7.

## Changes

- Optional `EngineOptimizations.NativeBuildLogGuard` prevents native block-break audit formatting from dereferencing a missing block code or null player. It writes the numeric missing ID or `<system>` while retaining the native Air branch, position, log format and single logger call. It does not replace blocks, restore inventory or change Suite BlockLogger storage.
- Default **off**. Enable independently from the Suite Optimizations safety group, or optimizer.json. Incompatible method layout, existing/late foreign Harmony dependency patches and runtime failures leave or restore stock handling. Failures are preserved, not swallowed or replayed. Actual module state and missing-code/system-player/failure counters are exported.
- Suite exceptions now include their first non-framework stack origin in the grouping key. The official native `ServerSystemBlockLogger` is no longer mistaken for the Suite's `BlockLogger` namespace.
- Old mixed message-only totals remain historical legacy aggregates. They are not reassigned to a mod. New bounded 5-second buckets show approximate live 5-minute observations, with rate warmup and coverage. Archived/pre-start events do not contribute to these rates.
- Immutable mod-alias snapshots remove a repeated list clone from live error attribution.
- Performance Center reports the retained incident peak separately from sampled P95/max, so a severe archived stall is not hidden by coarse sampling.
- Performance Center exposes missing engine profiles and absent/stale manual entity scans. It does not start additional profiling or population scans.

## Evidence and limits

The supplied diagnostic capture contains native build-logger null-reference stacks, mixed historical exception groups, no completed engine profile, and an old manual entity scan. It also contains both strongly GC-correlated stalls and a much larger stall with little measured GC pause. This pass targets the demonstrated exception path and misleading attribution; it does not claim to resolve every stall or prove production TPS gains.

No forced collection, incremental autosave, world-generation policy, entity scheduling or world data format changes are introduced. Existing compatibility blocks, including foreign ownership of server-asset compression, remain enforced. Test on a recoverable server copy before changing optional runtime patches.

## Validation

- Official 1.22.7 / .NET 10: 26 optimizer test suites pass, including 13 native audit behavior/failure checks and 107 engine lifecycle/conflict checks.
- Suite Release build: zero warnings/errors. 22 root checks and 11 bounded log-evidence checks pass.
- Browser checks pass for NOS settings/locks/mobile layout, escaped stack origins/live counts/rate warmup, missing/stale evidence and separate retained peaks, filters and incident navigation.
- Copied official server with 25 mod ZIPs: 92 checks pass, including real terrain/A*, independent opt-in guard activation/deactivation, same-message/different-stack grouping, correct native ownership/recent counts, and a synthetic retained 44-second incident peak independent of live sampling.
- Results are sanitized synthetic validation artifacts in [research/diagnostics-0.17.0](research/diagnostics-0.17.0/validation.json). The private uploaded bundle and production logs are excluded. No authenticated-player workload or production TPS claim is made.
