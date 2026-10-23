# NOS 0.16.1-preview / Suite 1.2.48 Optimization UI2

This is a targeted improvement to the existing optional collision-generation patches and their Suite controls. No new module is enabled by default. NOS still patches the inspected official 1.22.7 server in memory; it does not replace the runtime.

## Runtime change

Before 0.16.1, every enabled generation call inspected the block-accessor type before testing whether native bounds were unchanged, and incremented two atomic counters on a cache hit. Now the native integer bounds are computed and compared first. An exact bounds hit returns without inspecting the accessor or borrowing a callback. One atomic hit increment suffices; total optimized calls are reported as regenerations plus hits.

This follows native BlockPos.SetAndEquals: when coordinates match, it returns true without assigning anything. The helper is now an explicit conflict dependency. Bounds remain untouched on a cache miss until the official synchronous walker is verified, so a custom/null/unsupported accessor's native fallback does not mistakenly observe warmed bounds and skip its walk. Both standard and cached testers retain native bounds arithmetic, dimensions, invalidation, box ownership, shape hooks and displacement resolution. It is not a stationary entity/physics bypass and introduces no cross-tick result cache.

Existing guards, independent controls, failure disablement, recursion/thread isolation and cleared callback references remain. Collision generation/traversal stays off by default. Regeneration CPU timings remain mixed; native-equivalent lower allocation does not imply a CPU gain for every workload.

## Component evidence

The same official-method fixture, synthetic shapes and intercepted synchronous walker was run before and after the change with tiered compilation disabled. Four alternating native/enabled trials warm 4,000 calls and measure 30,000 calls each. The fresh 0.16.0 enabled cache-hit results were 25.65–26.03 ns/call; the initial 0.16.1 enabled results were 13.26–16.28 ns/call. These are microbenchmarks, not production TPS, GC-pause or autosave latency measurements. All trials are retained rather than selecting only the fastest.

Collision regeneration still avoids 96 bytes per call in the fixture, and cache hits avoid 32 bytes, as in 0.16.0. New behavior checks include warmed custom/null accessor hits, miss invalidation after a hit, fallback traversal after a miss and native ownership of custom-generated cached geometry. Earlier geometry/order/dimension, recursion, concurrency, animation and failure checks remain. Foreign SetAndEquals hooks block the shortcut rather than being bypassed.

## Control panel

Settings search expands matching groups and restores the previous open groups when cleared. Expand/collapse, search, pending-edit review and execution cards are local DOM operations with no extra server polling or automatic settings changes. Pending edits show server and draft values; returning to saved values clears dirty state. Empty/out-of-range numeric settings are rejected before a request, and capture/permission/compatibility locks remain. Actual module states have clearer badges. Cumulative execution cards distinguish observed work from latency or bytes saved; full counters remain available.

The autosave guide and native save transactions are unchanged in behavior. Save snapshot release reduces retained references after successful background saves; it does not implement incremental autosave or shorten serialization/disk I/O.

## Final validation

All 25 optimizer build/test suites, 54 collision/animation component checks, 105 lifecycle/conflict checks, 22 root checks, browser/mobile/draft/validation regressions and source consistency pass. Both mods compile with zero warnings/errors.

The [copied-server result](research/components-0.16.1/copied-server.json) passes 90 checks with 25 mod ZIPs. Both tester types execute bounds-cache hits on actual terrain, native NewTick invalidates them, native collision/animation work and Suite controls run, failures stay zero, settings are restored and shutdown is graceful. Tiered compilation is disabled in this correctness run; no authenticated clients or player-performance comparison is represented.

[Before](research/components-0.16.1/baseline-0.16.0.json), [initial after](research/components-0.16.1/initial-0.16.1.json) and [final component trials](research/components-0.16.1/collision-animation.json) are all retained. Final enabled cache hits range from 13.26 to 21.07 ns/call, including the slower third trial; pre-change enabled results range from 25.65 to 26.03 ns/call. Regeneration remains mixed against native. This supports a targeted component improvement, not a production percentage claim. Exact [runtime revisions and assembly hashes](research/components-0.16.1/validation.json) are recorded. Guides may be repackaged after validation; tested DLLs remain unchanged.
