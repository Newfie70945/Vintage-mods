# NOS 0.8.0 preview + Suite 1.2.47 Physics1

## Implemented

The official `PhysicsManager.SendPositionsAndAnimations` creates fresh position, animation and tag lists each pass. NOS replaces only these three constructions with per-thread temporary lists. The original method still selects tracked entities, constructs independent packet arrays, splits UDP positions into groups of eight and calls the original transports. It does not reduce update frequency, change wire format or reuse outgoing arrays.

A prefix leases a buffer set; a Harmony finalizer clears all references on normal return and exceptions without swallowing errors or retrying sends. Recursive calls lease separate sets and restore the outer context. At most one empty idle set is kept per participating thread. Any list capacity above 4,096 discards the entire set; disable/reapply invalidates idle generations. Old thread-local generations contain empty arrays, not entity/player references, and are replaced on next use. Supported official hashes, method contract/transpiler-layout guards, foreign ownership checks and late-conflict monitoring apply. The independent switch defaults on.

Suite adds the switch and pass/created/oversized counters, keeps older NOS settings absent and disabled, and clarifies unavailable external Tungsten/Synergy command controls. Suite remains 1.2.47 with build suffix `ms11-optimizer-physics1`.

## Component validation

The actual official 1.22.7 physics send method runs in a synthetic client fixture. Only transports are intercepted; these are not authenticated players or real network measurements. Tests compare position/animation contents and order, retained outgoing array ownership, tag identity, eight-packet UDP grouping, recursive calls, exception propagation, reference clearing, oversized discard, parallel leases and independent disable. Lifecycle tests include master/safe switches, foreign patches, monitor failure and teardown.

Three alternating unpatched/patched trials, 30,000 calls each after warmup, .NET 10 tiered compilation disabled:

| Fixture | Stock B/call | NOS B/call | Removed B/call |
| --- | ---: | ---: | ---: |
| One client, no tracked payloads | 96 | 0 | 96 |
| One client, 64 positions + 64 animations | 3768 | 1448 | 2320 |

The populated fixture retains allocations for independent packet arrays, packets and the proxy transport. CPU timing was noisy, especially for the empty fixture; individual empty trials were slower with NOS. Populated trial medians were approximately 3036 ns stock and 2304 ns NOS. These component results establish allocation reduction in these fixtures, not production tick/GC improvement or player capacity. [Raw results](research/components-0.8.0/physics-packet-measurements.json).

## Copied-server integration

All 17 optimizer test suites passed (13 C# projects and four Python suites), plus browser controls/older-NOS/read-only/mobile regression checks. A copied official server with Suite, NOS, a disposable probe and the selected third-party pack (25 ZIPs, 28 loaded mod records) passed 12 integration checks. The physics module activated and independently disabled/re-enabled through the authenticated Suite route; the new setting appeared with the expected default. Existing real-terrain official-A* checks, profile capture protections and settings restoration passed. [Sanitized result](research/components-0.8.0/suite-real-terrain-integration.json).

This was startup/control and sampled correctness validation without connected players. It does not establish full mod interaction coverage, long-session behavior or production performance. The separate transport-intercepted component fixture exercises populated physics packet construction; server integration does not substitute for a real-player workload comparison.

## Other candidates inspected

The official inventory sender already checks `IsDirty` before serializing slots. Avoiding its outer inventory scan requires reliable tracking of custom inventories and dirty changes; it is deferred. Pathfinding node pooling requires an ownership/reset audit across search outcomes and threads. Physics client-list reuse also needs care because the helper thread can still be using a previous list after the main-thread wait limit. None of these changes is included or advertised as active.

The methods listed by Tungsten/Synergy inspired the target selection; no third-party code or binaries are included. NOS still blocks unvalidated stacking with those optimizers. Earlier NOS behavior and validation limits remain documented in performance-0.7.0.md.
