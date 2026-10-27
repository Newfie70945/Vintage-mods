# Performance Center refresh audit — 1.2.53

Build: `ms11-performance-refresh1`. Includes the preceding glider and clumping audit fixes; companion modules and NOS are unchanged.

## Findings and changes

- Expired overview/timeline/page snapshots used stale-while-refresh delivery. The first request could start fresh work but receive the old graph; another click was needed to retrieve the completed replacement. These three Performance endpoints now await their existing coalesced replacement when expired. Fresh entries still keep their original TTL (overview 1 second; timeline/page 5 seconds). Other routes keep their prior behavior. A failed fresh build returns a request error and retains the last cached payload.
- A delayed full-page response could replace newer live readings at the same range. Full-page requests now reserve overview/timeline revisions before awaiting the response; individual overview reads also have revision guards. Compatibility fallback keeps its originally requested range and guards navigation changes.
- Refresh Overview fetched the entire page and historical BlockLogger activity. It now uses the lightweight overview/timeline refresh path. Visible refresh buttons show pending state and automatic polling waits for full-page loading to finish. Existing two-GET browser concurrency and backend coalescing remain intact.
- Successful alternate refresh paths could leave obsolete failure messages behind. Freshness error state now recovers across the full-page/lightweight routes. A graph refresh failure preserves the previous graph and its error remains visible across redraws. The graph also shows its latest sample time.

## Validation

- CoalescedSnapshotTests: 64 expired readers wait on one replacement; the first response contains new data; fresh hits retain TTL; failures and paused reads preserve cached data; existing serialization, cache bounds, invalidation and lifecycle tests retained.
- Browser tests: same-range full/live/overview races; Refresh Overview avoids full-page and block-history endpoints; retained error/recovery state; actual automatic scheduler, edit/hidden/safe-mode gates; timeline gaps, ranges, resize/reveal recovery and incident navigation.
- Compiled runtime tests: cache ownership/bounds, request timeout behavior and existing speed-policy checks.
- Official-server integration: expires the real page snapshot cache, checks that the first next response has a newer generation time, exercises reading endpoints, and saves/stops the disposable world.

This pass audits the Performance refresh path, not every implementation in the mod suite. It does not start automatic profiler captures, entity scans, terrain loads, exports, collections or database-history polling. Diagnostics still need to be enabled for new samples. Auto refresh follows the browser's saved Performance-page setting and pauses while the tab is hidden or Safe Diagnostics is enabled.
