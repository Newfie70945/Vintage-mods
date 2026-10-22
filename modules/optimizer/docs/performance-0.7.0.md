# NOS 0.7.0 preview + Suite 1.2.47 OptimizerEntity1

The retained integration capture used the initial 1.2.46-labelled candidate. The release identity is corrected to 1.2.47; optimizer behavior is unchanged.

## Implemented changes

- Independently switchable nearest/interactable entity searches reuse a per-thread closure/delegate context. The official partition walker, strict distance/tie rules, filter order and exceptions are retained. Reentrant queries check out separate temporary contexts. Finally clears entity, position and predicate references, even on errors; only one idle context per thread is retained. Disable/reapply invalidates generations.
- The worker's path-processing cap is checked before taking the next request. In the inspected official loop, `Next()` is evaluated before `maxCount-- > 0`; hitting the limit can dequeue an unprocessed request and leave it unfinished. NOS changes that narrow dequeue operation, retaining the stock worker body and A*.
- Optional main-thread assistance defers while the same queue head has not been observed long enough, then uses a count cap and soft elapsed-work budget. Defaults: off, 300 ms observed-head grace, one search, 2 ms soft budget. This is not original enqueue age; worker progress can reset the observation. One A* call is non-preemptible and may exceed the budget. Waiting paths can complete later. The helper passes each task's creature type to official A* and marks a failed search complete with no path rather than silently abandoning it. A path exception trips assistance off, increments a failure counter and lets the compatibility monitor remove that module until settings are reapplied.
- `/optimizer profile clear` and Suite's **Release Stopped Capture** release stopped report references. Running captures are refused. Existing files and independent export snapshots remain. No `GC.Collect`, compaction, no-GC region or global GC setting is used; immediate RSS reduction is not promised.
- Suite controls expose switches, soft budget/count/wait inputs, actual module activation and cumulative execution counters. Older optimizer versions do not receive unsupported settings. Read-only/Safe Diagnostics/capture-running restrictions remain.

All engine patches retain exact official 1.22.7 version/hash checks, installation diagnostics, independent gates, foreign-target checks and own-patch removal. Full-engine optimizers remain conservatively blocked. NOS patches the official runtime in memory; it does not replace or edit server API DLLs.

## Component results

Official 1.22.7 methods, actual partition walker, a synthetic API proxy fixture and 16 entities; .NET 10 tiered compilation disabled, five alternating stock/optimized trials. The remaining allocation includes unchanged walker/proxy overhead. Timing depends on host contention; initial single trials under concurrent build load were noisy and sometimes slower. These paired component medians are not production tick/GC improvements.

| Search | Stock ns/call | Reuse ns/call | Stock B/call | Reuse B/call | Removed B/call |
| --- | ---: | ---: | ---: | ---: | ---: |
| Nearest | 310.373 | 298.376 | 400.00032 | 288.00032 | 112 |
| Interactable | 322.466 | 301.952 | 488.00032 | 288.00032 | 200 |

[Raw alternating trials](research/components-0.7.0/entity-search-measurements.json). The exact allocation reduction was stable across the recorded runs. How much a real server saves depends on how often these queries execute; counters do not estimate bytes or prove a TPS change.

## Correctness and integration

- Search regression: actual stock traversal, matching results and callback order, null/no-result behavior, recursive calls, thrown predicates without retry, parallel calls, cleared input references, independent disabling and allocation comparison.
- Queue regression: reproduce the dropped 101st request with a cap of 100; fixed worker retains it and eventually completes all requests. Covers zero cap, grace deferral, assistance count, creature type, failed path completion and shutdown. The component solver is stubbed; it makes no terrain performance claim.
- Engine lifecycle/conflicts: 37 tests including foreign partition walkers and path-worker targets, safe/master switches, late conflicts, listener failures and own-patch cleanup.
- Capture release: active-capture refusal, stopped-reference release, independent report snapshot preservation and idempotence.
- Browser controls: settings/draft preservation, actual conflict state, capture release capability, older-NOS compatibility, read-only/Safe Diagnostics restrictions and mobile layout.
- Copied official server with Suite, NOS, the integration probe and selected third-party pack (25 ZIPs total): 11 checks passed. Actual A* on loaded terrain reproduced stock queue loss, completed 101/101 fixed requests, limited assistance to one search, and matched a directly computed official path for the tested terrain case. Suite independently applied settings, rejected invalid budget values, refused active-capture removal and released the stopped report. Original settings were restored. [Sanitized result](research/components-0.7.0/suite-real-terrain-integration.json).

The selected pack includes the Suite companions plus Floral Zones, Primitive Survival, xSkills/XLib, Draconis and Forgotten Armory components, among others. This integration confirms startup/control compatibility and sampled correctness, not player capacity, long-session behavior, all creature/path combinations or TPS improvement. Existing broader 0.6.0 laboratory results are historical, not validation of the new scheduling policy.

## Inspiration review

Reviewed [Opttage Path 0.2.3](https://mods.vintagestory.at/opttagepath) as requested. Its description highlights path-queue pressure, search reuse, distributed food searches and repeated creature-shape work. Those are useful research targets. NOS independently inspected the official engine's dequeue order and implements its own narrower worker fix and optional assistance policy. No Opttage code or binaries are shipped, and NOS does not claim its published benchmarks. Walkability/closed-region caching, food jitter and shared shape hydration are not implemented here.

## Staging recommendation

Install matching Suite/NOS builds on a copied world. Check actual module states in Optimizations; capture matched workloads with comparable player activity. Leave main-thread path assistance off initially. Enable it separately only when path overflow is measured, then compare long-tail ticks, path completion delays, allocation rate and measured GC pauses. Keep a stock/default control run. Do not infer retained-memory leaks from committed heap/RSS alone or claim production performance from these component tests.
