# Suite 1.2.48 MemoryPerf2 / NOS 0.9.1 preview

The invisible terrain report was identified by the owner as Better Crates and Wool. This pass leaves terrain/send limits, worldgen order, packet contents, AI scheduling and GC mode unchanged.

## Implemented

- Stream the large internal Performance/Operations JSON archives into unique temporary files and replace atomically after success. Avoid whole-file UTF-16 strings; preserve prior state on serialization/write failure.
- Use compact incident rows for overview refreshes and marker-only incident/mod-change events on the timeline. Full archives remain in storage, the explicit bundle and the clicked-spike endpoint. Filtering, comparison and navigation retain their required fields.
- Exclude unused profiler-table copies and mod-loader enumeration from background maintenance snapshots. Reuse unchanged five-minute summaries for at most one second. Manual memory snapshots read the latest observed state.
- Keep elapsed/queue waits out of the quick-read execution ranking as well as the timing table.
- Show whole-process allocation rate between same-process memory snapshots. Cross-process, legacy, zero-interval and reset-counter pairs do not produce a rate.
- Reduce temporary collections, duplicate metadata reads and unrelated failure-counter reads in NOS's once-per-second compatibility monitor. Module guards and stock fallback remain enabled.
- Explain current coverage and missing Synergy/Tungsten behavior in the optimization page and [coverage audit](../modules/optimizer/docs/replacement-coverage.md).

## Measured scope

| Fixture | Before | After |
| --- | ---: | ---: |
| 5,000-row state serialization/write allocation | 6,859,299 B/write | 699,216 B/write |
| Actual-Harmony optimizer monitor allocation | 85,480 B/check | 34,816 B/check |
| Synthetic incident JSON with 45 observations | 21,999 B/full archive | 365 B/list row |

These component fixtures do not establish production TPS, long-session heap reduction or the cause of the owner's ~75 MB/s whole-process allocation rate. Background snapshot table-copy savings are modest (83,584 to 80,960 B/read in the seeded fixture); timing varied under concurrent builds.

## Validation

Both release builds: zero warnings/errors. All 18 NOS test suites pass, including 39 lifecycle/conflict checks. Suite runtime checks cover monitoring/diagnosis equivalence, summary reuse/invalidation, full profiler preservation, compact list identity/evidence filtering and retained drawer details. State tests cover JSON equivalence, failed serialization, failed replacement, cleanup and successful replacement. Browser checks cover allocation-rate edge cases, compact-row filtering/comparison, legacy evidence flags, execution/wait presentation and existing timeline recovery.

The copied official 1.22.7 server run passed 22 checks and uses the Suite/NOS pair and the selected modpack (25 ZIPs / 28 mod records). It seeds synthetic incident archives to test compact overview/marker payloads and independently fetched full observations, then exercises actual save markers, shared warm responses, animal-rule persistence and official A* on real terrain. These archives are test data, not real lag measurements. Both streamed state files parsed successfully after graceful shutdown and no temporary files remained. [Raw sanitized results](validation/memory-perf-1.2.48.json).

The owner's latest readings show substantial fragmentation, a decreasing recent post-Gen2 floor and a high allocation rate. They do not prove a leak in this window or identify a particular mod. NOS currently has allocation-focused engine modules but is not a complete replacement for distance-based activation/AI throttling, node pooling, batching, recipe/container or terrain-noise features. See the coverage audit for each area.
