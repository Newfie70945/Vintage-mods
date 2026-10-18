# NOS 0.4.0 — actual entity and AI hot-path changes

Both modules run while profiling is OFF. They do not slow entity tick rates, remove tasks, defer physics or change gameplay ranges.

## AI filter dispatch

The official `AiTaskManager.ShouldExecuteTask` builds an invocation-list array on every filtered execution decision. NOS enumerates the immutable delegate list without constructing that array. It calls all handlers in their original order, preserves veto aggregation, subscription changes and exceptions, and retains the original immediate true-return path for managers with no filters.

## Repulsion hot path

The official repulsion tick creates a walk callback and an always-true range callback each time. NOS reuses the walk callback through an ephemeron/weak-key table. It passes null for the range callback, selecting the official partition walker's existing unfiltered branch instead of calling a predicate that unconditionally returns true for every neighbor. The private predicate's exact two-byte `return true` IL is verified before activation. Foreign patches on the predicate, collision callback, walker or tick block this module.

Original collision calculations, iteration order, dimension checks, custom mod repulsion callbacks, movement and mounting checks remain. Weak-key collection is tested so callback caching does not keep despawned behaviors alive.

## Component comparison

Actual official 1.22.7 methods before/after Harmony injection; five alternating-order trials, 30,000 warmup calls and 300,000 measured calls per mode. Tiered compilation disabled to avoid comparing different JIT tiers. These are synthetic fixtures, not bots, real player load or a TPS benchmark.

| Component | Stock median | NOS median | Allocation change per call |
| --- | ---: | ---: | ---: |
| AI check with two filter handlers | 37.1 ns | 17.5 ns | 40 B → 0 B |
| AI check with no handlers | 4.48 ns | 5.51 ns | 0 B → 0 B |
| Repulsion tick with 16 nearby creatures | 409 ns | 385 ns | 128 B removed |

The unfiltered AI difference is about one nanosecond in this fixture; no improvement is claimed for an already-empty filter path. Repulsion CPU results varied, including one slower trial; median improvement is about 6%, while the 128-byte allocation reduction was consistent. World/block-access mocks add 192 B per repulsion call to both modes; those allocations are fixture overhead, not an engine allocation claim. Avoid extrapolating these numbers into whole-server speedups.

Tests cover bit-identical ordinary collision output, custom repulsion, dimensions, inactive entities, full veto invocation, mutation during filter execution, callback exceptions and weak-key reclamation. Independent switches, exact official API/server/Essentials hashes, Harmony conflict checks and teardown apply to both modules.

[Raw measurements](research/components-0.4.0/entity-ai-measurements.json).
