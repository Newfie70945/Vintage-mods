# NOS 0.11.0-preview / Suite 1.2.48 NOS Broadcast1

This pass adds opt-in empty-exclusion broadcast fast paths and entity cleanup/despawn temporary-list reuse. All earlier modules remain. The official engine handles sends and full entity ticks. This is not TCP batching, distance send-frequency reduction, full entity sleep or a client protocol replacement.

The supplied diagnostic bundle identifies Suite 1.2.47 and NOS 0.7.0, predating MemoryPerf2 and Distance1. Its 100 retained incident groups mix genuine GC pauses and stalls without matching engine profiles. Queue length, save overlap and low whole-process CPU do not identify executing code. No single offending mod can be established from that bundle. An owner report accompanies the compiled download; the original bundle and private player/location data are not committed.

All 20 optimizer build/test suites pass; Suite compiles with zero warnings/errors. Headless Chromium passes new controls and existing permission/draft/capture/mobile checks. Twenty incident-evidence checks pass, including a reduced long-stall/short-pause/weak-queue regression case. Component fixtures intercept transport; they are not authenticated players and do not establish production TPS or total heap improvement.

## New module behavior

Both switches default off. The broadcast group has separate raw, packet and UDP target statuses. It rewrites only an empty method-local IServerPlayer[] argument to null; this avoids stock predicate delegate construction while running the entire original method. It does not mutate the caller array. Nonempty filters, null entries, callback exceptions and partial send failures retain stock behavior. No send-loop replacement, private send delegates, TCP batching or reduced delivery cadence is used.

Lifecycle reuse wraps the two official entity simulation methods with nested/thread-local temporary scopes. Only verified local list constructors change. Successful and failed scopes clear entity references, capacities above 4,096 are not retained and stock ToArray calls produce independent packet buffers. Full entity ticks and queue/sending semantics remain.

## Component evidence

The test calls the real official broadcast overloads and real packet serialization, intercepting only transport. It compares null, empty, nonempty and null-element exclusions, offline/queued filters, packet bytes/order and dummy-connection preparation resets. It checks callback exceptions and no in-call replay after partial send failure. Lifecycle tests check actual injected methods, per-client despawn payloads, queue clearing, nested scopes, cleanup after exceptions and oversized-buffer release.

| Raw broadcast, 32 fixture clients | Allocated bytes/call | Elapsed ns/call |
| --- | ---: | ---: |
| Stock | 2848.06 | 4620.18 |
| Enabled | 800.00 | 550.39 |
| Enabled | 800.00 | 565.35 |
| Stock | 2848.00 | 1160.35 |

The empty-exclusion fixture allocates about **72% fewer bytes**; optimized timing trials are faster in this component run. This is not a transport benchmark or a production TPS percentage. Empty entity-cleanup ticks fall from about 32 bytes/call to zero after warmup, with a small CPU overhead in this fixture. Lifecycle pooling remains opt-in: fewer allocations do not guarantee a CPU gain, especially when little cleanup is needed.

[Component measurements](research/components-0.11.0/broadcast-lifecycle.json) retain all values. The reduced empty-list fast path was chosen after an exploratory whole-loop implementation showed CPU overhead; that send-loop rewrite is not shipped.

## Copied official server validation

[Thirty-nine checks pass](research/components-0.11.0/copied-server.json) on official 1.22.7 with the Suite, all Suite companions and the selected 25-ZIP pack. They verify group defaults, all five new target states, independent disable/apply, actual broadcast fast-path counters, normal cleanup scopes, prior path/animal/timeline behavior, and a real diagnostic ZIP whose JSON entries decode successfully. Exported NOS settings/status and retained native tick-debug state are present, and exporting does not start profiling. There are no authenticated players; loaded mods are not equivalent to exercising every feature.

Suite ZIP entries now serialize directly to the compression stream, avoiding an intermediate full JSON string for each entry. A final compressed byte[] remains necessary for the current response API. Incident attribution version 7 prevents weak queue/save context from outranking insufficient execution evidence, re-evaluates retained incidents once and allows actual overlapping ticks up to five minutes while retaining an eight-tick result cap. Missing historical execution profiles cannot be recreated.

Representative player workloads and longer matched soaks remain necessary before making aggregate CPU, heap, TPS or capacity claims. Full entity activation/sleep, TCP flush consolidation, tracking/send-frequency policies and negotiated client delta updates remain outside this pass.
