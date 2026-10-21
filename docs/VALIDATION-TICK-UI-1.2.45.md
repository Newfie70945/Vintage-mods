# Suite 1.2.45 TickUI1

Build: `newfies-server-essentials-suite-1.2.45-ms11-tick-ui1`. Retains StorageSave1 and Performance1. Companion versions and owner settings are unchanged.

## Tick Debugging

During a bounded engine capture, the page ranks the top 12 sampled leaf markers by accumulated time. It shows candidate owner/confidence, total milliseconds, maximum segment and call counts. Native slow-tick logging alone does not collect these engine-profiler timings. Turning the page's action gate ON does not start continuous profiling.

The existing three-second page refresh requests the ranking as part of the existing tick-debug payload. The backend reads bounded existing accumulators under the profiler lock, resolves at most 12 owners through the existing attribution cache, and returns detached row values. It does not enable/read the native profiler tree, rebuild all mod aggregates, add a timer or make another HTTP request. On completion, it shows the last retained hotspot results with the completion timestamp and sampled tick count. Sampling can miss intervening ticks; ownership is marker evidence, not proof of a mod defect or percentage of whole-server CPU.

A quick investigation guide explains how to enable lightweight diagnostics and start Incident Capture. Shortcuts link Performance, Full Profiler, Memory/GC and Database Storage. Grouped smart diagnosis is labelled as the recent five-minute window, while the event table retains its selected range. Read-only users see capture/native-action controls disabled; server route authorization remains unchanged.

## Player Commands

Individual switches now say Enable command / Disable command, status badges say Command enabled / Command disabled, and the availability filter uses the same terms. Accessible button names identify the command. The external provider/fallback note remains because the switch cannot disable another mod's root command. No command routing or config semantics changed.

## Validation

- Release build against official Vintage Story 1.22.7 / .NET 10: zero warnings/errors.
- Browser checks: live versus retained contributors, sampled tick counts, hostile marker escaping, no automatic profiling writes, read-only capture/action controls and mobile width.
- Existing performance/tick history browser regression passes.
- Full Player Commands desktop/mobile regression passes, including new button text, search/provider filters, lazy reads, draft preservation, permissions, read-only/busy behavior and isolated warp settings saves.
- Shipping-assembly fixture reads actual live accumulators: bounded rank, ownership, sample count, timing share, retained state and non-mutating reads. The fixture intentionally has no game API/profiler, so the read cannot silently start profiling. It seeds timing data; it is not a player-workload benchmark or end-to-end engine capture test.

Reproduce the backend fixture after building:

```sh
VINTAGE_STORY=/path/to/official/game NSS_ASSEMBLY=/path/to/built/NewfiesServerEssentialsSuite.dll dotnet run --project modules/suite/tests/runtime/tick-contributors/TickContributorChecks.csproj -c Release
```

Further UI priorities identified: a unified incident evidence panel; consistent selected time ranges across all diagnostic summaries; explicit dirty/saved state on remaining legacy settings pages; stronger player-admin search/action grouping; and a reviewed storage-cleanup preview. These are follow-up areas, not features claimed in this pass.
