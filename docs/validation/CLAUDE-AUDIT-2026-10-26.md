# Claude branch audit — 2026-10-26

Review baseline: `9c3d9ef` (Suite 1.2.52 / loaded-map1). The author subsequently confirmed the previous versions were released and requested versions ahead of them: Suite **1.2.53 / ms11-claudeaudit2** and NOS **0.20.15-preview**. The optimizer retains its engineering-preview maturity designation; 0.20.14 is the author-reported baseline. Companion modules are unchanged.

Fetched all remote refs. New relevant work was found on `webmap-exporter-improvements` (`b0ea521`) and `claude/optimizer-mod-audit-m5la11` (`e7555d8`). Earlier optimizer audit fixes were already incorporated into main and were preserved. Sailing (`ccr-aa5de6be-3y48gz`) is explicitly excluded at the author's request. Other previously reviewed branch tips have no new changes.

## Accepted

- Claude's opt-in barrel failed-recipe memo and position-phased forced rescans (`678197e`, `e7555d8`). Exact official type/IL, assembly and foreign-hook guards remain authoritative. Stack identity, collectible and count changes force searches; active recipes, custom barrels and changed inventories retain native handling. Weak-key storage does not root unloaded barrels. Failure propagation is not replayed.
- Added a follow-up guard: exceptions during the skipped unseal/MarkDirty remainder disable the module too, even when native search state was not entered. Native fault-injection tests cover that path.
- Suite controls, setting bounds and counters for the barrel option. **Off by default**. In-place item attributes or hook/world-state-only changes can delay recipe recognition up to `BarrelRecipeRescanEvery × 3 seconds` (30 seconds at default 10). This is a behavior tradeoff, not an exact-timing-preserving optimization.
- Claude's caller-buffer PNG decoder for map pyramid children, using one reusable child image per pyramid operation rather than allocating each decoded image. Added bounds and complete-header/chunk/IEND checks. Buffers return in finally; no new long-lived overview/coverage/thread-local stream cache.
- Claude's edge-span hillshade correction. It improves border shading on continuous slopes; it does not promise seamless arbitrary terrain across independent tiles. Existing images change only when rendered again.
- Claude's missing route policy registrations for mod-update compatibility and automatic profiling, plus source/version/clone/route checks. Adapted the cache-version test to our existing shared constant instead of restoring duplicated numeric constants.
- Author follow-up: translocator overlay defaults off on **every page open**, overriding previously saved translocator visibility but preserving other layer preferences. Discovery, server enablement and data remain unchanged; users can enable the overlay for the current visit.

## Withheld from main

- Row-count-only generated-coordinate cache (`9cd0dba`): the same row count does not establish identical coordinates. Delete/replace operations could reuse stale coverage for up to 30 minutes. Also avoids retaining a large global coordinate set and adding a count query on every pass.
- Shared overview scratch and per-thread compression streams from `3c0a13d`: retained large buffers and global/thread lifetime complicate memory release. Only independently tested caller-buffer decoding was adopted.
- Water-depth raw-tile extension and color-override globals from `8b56b6b`: the extra raw channel/version change is not integrated with main's resident-only capture. Merging wholesale would invalidate compatible maps and produce inconsistent depth on loaded updates. The cache-version mismatch itself was already fixed on main through a shared constant.
- Sailing: untouched, per explicit instruction. No sailing code, assets, registrations or solution entries are merged.

## Validation

Official Vintage Story 1.22.7 / .NET 10. Full optimizer driver: 36 test suites; barrel memo fixture: 24 checks. Synthetic 1,000-barrel / 300-recipe trials reduced average work from about 22–28 ms to 2.2–2.9 ms per round, and allocations from 67,520 to 6,752 bytes per barrel callback. These are component workloads, not a production TPS claim.

The map PNG fixture compares eight warm 256-pixel noisy tile decodes: allocating decoder 10,513,968 managed bytes; caller-buffer decoder 4,800 bytes, excluding the reusable output buffer's creation. Pixel equivalence, repeated writes, wrong dimensions, garbage and truncated files are checked.

Map memory/native tests retain zero world-loader calls, unloaded/concurrent edits, partial-capture safety and unchanged save data. WebMap privacy/lifecycle, translocator rules and module safety regressions run against the compiled Suite. Browser coverage checks optimizer drafts/unsupported fields, translocator reopening/privacy, incremental tile invalidation and performance refresh. Disposable server integration additionally verifies live barrel settings enable/disable, all seven mod IDs, capture lifecycle, explicit save, clean shutdown and copied-world integrity. This does not replace production modpack testing.
