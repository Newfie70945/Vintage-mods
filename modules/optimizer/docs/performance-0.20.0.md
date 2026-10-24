# NOS 0.20.0 — optional queued block-simulation budget

The supplied native logs repeatedly measure hundreds of milliseconds in ServerSystemBlockSimulation.OnServerTick, including a 762 ms interval. Inspection of the official 1.22.7 body shows it drains a FIFO queue under a callback-count cap, without an elapsed-time limit. Some callbacks may be expensive. This pass changes that exact native loop when explicitly enabled; it does not claim to fix all causes of lag.

## Controls and behavior

Suite 1.2.49 ms11-block-budget1 exposes `BlockSimulationBudget` (default false) and `BlockSimulationBudgetMs` (default 5, range 1–20). Enable Spread native queued block ticks across frames only for controlled testing. It is a scheduling policy, not an allocation-only optimization: queued block/fluid/growth work can run later, which can affect gameplay timing. Existing settings/defaults remain otherwise unchanged.

The patch replaces only the native queue-empty condition. On a nonempty queue it permits progress, subject to the original native callback cap, and ends that pass after the soft deadline. It does not dequeue, drop, deduplicate, reorder or reconstruct pending callbacks. Solid, fluid and extra-object dispatch, native error logging/catching and the count cap remain in the original engine body. Queued work remains for later ticks. One expensive callback or blocking operation can exceed the budget; nothing interrupts a callback already running.

This smooths batches rather than reducing their total CPU work. It cannot sustain a producer rate above the budgeted consumer rate. If a pass starts with more than 8,192 pending entries, the policy disables its gate immediately and restores stock processing without clearing the queue. The module monitor reports a backpressure-specific failure reason; it stays off until settings are reapplied. Inspect last-observed queue/peak, budget-hit passes and failures before deciding whether to increase the budget or leave it disabled. A stock count-cap exit can coincide with a budget hit; hit counts do not measure exact delayed callback counts. Queue metrics are observations, not continuous live measurements.

## Compatibility and ownership

The native method's exact inspected IL SHA-256 is verified. Dependencies include the target and queue IsEmpty/TryDequeue methods. Foreign dependency patches, unsupported runtime/contracts and unexpected exceptions retain stock behavior. Exceptions are propagated, not replayed. Only this target's call site changes; other queues are not patched. Scope state is thread-local, nested calls restore their parent scope, and the module retains no world/chunk/position references. Disabling it returns the untouched pending queue to stock processing.

The policy does not patch random-sampling/RNG algorithms, generation, save formats or disk persistence; delayed execution can still affect simulation timing and outcomes. It does not cover separate single-delayed fluid callbacks (`sdcb`), barrel listeners, block-entity timers, packet handling or the external The BASICs chat-history save callback. It does not establish a fix for the 37-second gap in the supplied log. Existing Suite anti-cheat/cull/profiler improvements remain included.

## Validation approach

Tests invoke the actual official OnServerTick body with stubbed world/block callbacks. They check FIFO and retained pending work, eventual completion without duplicates, native count cap, non-running phase, solid/fluid/extra dispatch, native caught exceptions, callback enqueues, nested/thread isolation, disabling mid-pass, unexpected failure propagation and backlog fallback without drops. A single expensive-callback fixture proves that the limit is soft.

The comparison fixture uses 60 callbacks with artificial 2 ms work each. It measures distribution across passes, not a real player workload, actual server TPS or a reduction in total simulation cost. Test on a world copy first and compare tick tails plus queue growth under matched workloads; do not enable overlapping patches or bypass compatibility guards.

## Results

Official 1.22.7 / .NET 10 builds pass all 29 optimizer suites, including 17 actual native queue-loop cases and 121 module lifecycle/conflict checks. Browser tests cover the independent checkbox, numeric setting, saved drafts/permissions/locks, unsupported older NOS and displayed queue/budget counters. Suite builds with zero warnings/errors. The copied official server with 25 mod ZIPs passes 101 integration checks including independent native budget activation/disable through Suite controls.

In the native artificial callback fixture, a stock batch took 123.22 ms; budgeted processing completed the same ordered callbacks across 20 passes, with a maximum observed slice of 6.15 ms and summed processing time of 120.50 ms. Separate tests demonstrate a callback can exceed the soft limit. These are scheduling fixtures, not a representative multiplayer workload or a claim of lower total simulation CPU. [Native fixture](research/block-budget-0.20.0/native-loop.json), [sanitized integration](research/block-budget-0.20.0/copied-server.json).
