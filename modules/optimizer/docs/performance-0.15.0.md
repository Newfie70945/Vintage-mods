# NOS 0.15.0-preview / Suite 1.2.48 NOS Traversal1

This pass optimizes work in official collision and container loops. Three independent opt-in switches default off: `CollisionTraversalReuse`, `ContainerTransitionScan` and `ContainerTickTraversal`. Configure them in Suite → Optimizations → **Collision and container traversal**. Earlier NOS modules remain; Suite version remains 1.2.48 with build `ms11-nos-traversal1`.

## Runtime behavior

Only private traversal locals and their factory/MoveNext/Current/Dispose calls change. Method-local structs avoid the native iterator object; original loop bodies, branch order and try/finally remain. There is no shared pool or retained geometry/inventory cache.

Collision still generates its box list, resolves displacement, updates flags and invokes block hooks, including zero-motion calls. It does not implement a stationary collision bypass. Container scans/ticks retain Count/indexer access, virtual transition requirements, room/chunk checks, food updates, synchronization and mid-tick-cleared handling. Custom inventory GetEnumerator overrides retain the original iterator, order and disposal; local wrappers dispose fallback iterators and clear references even if disposal throws. Public iterator factories and external callers are not patched.

Exact official 1.22.7 identities and expected local/loop/finally shapes are required. Foreign hooks on the consumer, native factory or generated iterator state machine block affected patches; late conflicts remove only affected own hooks. Unexpected layouts retain stock behavior. Exceptions remain visible, disable the affected gate and are never replayed after collision/transition side effects. Configuration changes do not interrupt or reuse an active iterator.

## Component measurements

[Raw fixtures](research/components-0.15.0/traversal.json) call actual official consumer methods with synthetic geometry/inventories and intercepted room lookup. Each alternating trial warms 4,000 times and measures 30,000 calls with tiered compilation disabled. The fixture includes proxy and geometry allocation that NOS does not change.

| Fixture | Stock bytes/call | Enabled bytes/call |
| --- | ---: | ---: |
| Sixteen-slot nontransitionable scan | 424 | 384 |
| Sixteen-slot container transition loop | 568 | 528 |
| Native collision loop, fixed geometry | 144 | 104 |

Each verified loop removes a 40-byte iterator allocation in this fixture. Container CPU timings improved in the recorded struct trials; collision CPU times were slightly slower in these recorded trials despite lower allocation. This supports allocation reduction, not a guaranteed CPU gain or production TPS/GC/heap claim. Profile actual calls, allocation rate and tick tails at matched player/chunk/entity counts. New controls remain off by default.

Behavior checks cover zero/moving/dimension collision results and hook order, native/custom inventory order/disposal, clearing/sync callbacks, early returns, recursive/concurrent traversal, failure disablement/no replay, reference clearing, configuration changes and rejected IL. Full build and copied-server reports accompany the download. The test server uses a disposable copy; no production world or authenticated-player workload is represented.
