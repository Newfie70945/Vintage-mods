# NOS 0.16.0-preview / Suite 1.2.48 CollisionGen1

This pass adds three independent opt-in runtime patches. In Suite → Optimizations → Collision generation and animation, enable standard collision generation, cached collision generation or animation collision-flag scan individually. All three default off. Build: `ms11-nos-collisiongen1`.

## Behavior and compatibility

Collision generation reuses a callback context only when the accessor resolves to the inspected synchronous official BlockAccessorBase walker. Custom walkers retain native behavior and callback ownership. The pool holds at most one cleared context per kind per thread; recursive calls rent separately. Tester/accessor references are cleared in finally, and configuration epochs prevent active contexts returning to an obsolete pool. Shape results are not cached. Bounds arithmetic, center order, fluid/solid traversal, virtual shape callbacks and resolution remain native-equivalent.

Animation replaces only the collision-adjustment Enumerable.Any scan with a struct dictionary enumerator. It invokes the original predicate, preserves early exit and mutation checks, and falls back for other enumerables. Native OnFrame, matrix decisions, poses, triggers and tick frequency remain. No animation frames are skipped.

Exact official 1.22.7 hashes and method contracts are required. Foreign hooks on replaced methods/dependencies prevent affected patches; late conflicts disable affected modules. Failures preserve the original exception, disable the gate and never replay partially completed work. The repulsion path remains unchanged: cross-tick neighbour caching needs stronger movement and mod-compatibility evidence.

## Evidence and limitations

Component fixtures compare actual native consumers with synthetic shapes, a synchronous intercepted native walker and an animator proxy. They cover geometry, dimensions, ordering, cache hits, recursion, parallel independent testers, cleared references, custom-accessor fallback, animation frame/flag/trigger order, no-animator behavior, failure/no replay and rejected IL. Four alternating trials warm 4,000 calls and measure 30,000 with tiered compilation disabled.

In these fixtures normal and cached collision generation remove 96 bytes per regeneration, and cache hits remove 32 bytes. Animation scans remove 56 bytes per call; remaining proxy allocation is outside NOS. Collision CPU timings are mixed: some regenerations and most warmed cache hits are slower despite allocating less. New collision patches therefore stay off by default; profile actual workload before enabling. Animation fixture timings improve, but this does not measure whole-entity animation cost.

These results do not establish production TPS, player capacity, GC-pause or heap reductions. Validation reports record build checks and disposable copied-server coverage separately. No production world is modified, and loading a modpack does not exercise every feature.

The [initial copied-server fixture](research/components-0.16.0/initial-fixture.json) is retained. It expected unknown animation metadata to remain active, but native ServerAnimator removes it during OnFrame. The fixture now checks that native removal precedes the flag scan; runtime code and engine guards were not changed.

## Final validation

All 25 optimizer build/test suites pass, including 48 new component checks and 102 lifecycle/conflict checks. NOS and Suite compile with zero warnings/errors; 22 root checks, source consistency and browser/mobile/control regressions pass.

[Copied-server validation](research/components-0.16.0/copied-server.json) passes 88 checks with 25 mod ZIPs. Each new setting toggles independently through Suite. Actual terrain exercises both collision generators; a native ServerAnimator executes its frame and collision flag scan. All new execution counters advance and failure counters remain zero. Settings are restored and the disposable server shuts down gracefully. No authenticated-client workload or production capacity is represented.

[All component trials](research/components-0.16.0/collision-animation.json) and [exact tested runtime revision/assembly hashes](research/components-0.16.0/validation.json) are retained. Guides may be repackaged after validation; tested DLLs are preserved unchanged.
