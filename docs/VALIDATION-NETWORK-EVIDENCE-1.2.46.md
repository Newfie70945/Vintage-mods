# NetworkEvidence1 — Suite 1.2.46

## Audit conclusion

The supplied native log cannot identify a mod. Official 1.22.7 NetworkChannel.OnPacket invokes the registered handler and then emits a handlecustom marker using only the message ID. IDs restart at zero for every channel, and frame markers measure intervals since prior marks, not a dedicated handler stopwatch.

Our incoming ID 4 paths include AntiCheat HeartbeatPacket and Investigator BlockLoggerSettingsRequest. Normal heartbeats validate bounded metadata and update a session; they do not perform a world scan or synchronous database query. Settings reads are small; explicit setting writes persist configuration. ID 1 paths include public-player-list/AFK hellos and OreShield acknowledgement (only when active). Static inspection does not establish any of these as the observed culprit. The previously confirmed paused migration preparation issue was fixed in MigrationPause1.

## New evidence

Read-only prefix/finalizer timings around the official NetworkChannel.OnPacket dispatcher record channel, message ID and type, registered handler class/method and assembly, wall milliseconds, coincident process GC pause delta, payload length and exception type. Resolve only the known engine deserialization closure's handler delegate; no arbitrary object traversal, packet contents or player identity collection occurs. Ownership is where execution entered, not proof that this code caused all elapsed time.

Wall time includes deserialization, handlers, waits and other dispatcher patches. GC delta is a process-wide interval measurement with boundary precision limits; subtracting it produces wall time outside the observed pause, not CPU time. Timings can overlap frame markers or other intervals and must not be summed. Unknown handler/type resolution stays explicit. Unsupported dispatchers or hooks leave gaps.

Collection follows the existing diagnostics enable switch, without a new timer. Fast calls retain no records; intervals >=25ms enter a 256-record/120-second bounded queue. Identity cache is bounded to 512 channel/message pairs and validates delegate replacement. At most eight worst overlapping records attach to each incident. Archives preserve attached data; disabled diagnostics clears live references and invalidates in-flight records. Exceptions retain their original behavior; measurement failures fail open and update status.

## Validation

- Official 1.22.7/.NET 10 build.
- 21 shipping-assembly checks against the real official NetworkChannel, protobuf wrapper and Harmony dispatcher verify per-channel IDs, actual delegate ownership, original exception propagation, inactive and fast paths, bounded queue/cache, age pruning, generation invalidation and allocation bounds.
- 25 shipping archive checks verify channel evidence ranking and that a handler paused by GC is not blamed for its entire wall duration.
- Chromium tests verify channel/type/assembly display, wall-minus-GC calculation, escaped strings, copied evidence, timeline and navigation regressions.

Local 20,000-dispatch microbenchmark runs measured 24.4–28.4ms disabled / 28.6–28.9ms enabled with approximately 3.36 MB allocated in both cases (mostly the engine deserialization and test invocation). This is not a player workload, production overhead guarantee or TPS comparison. No live trace from the affected server was collected. The next production spike needs its Slow custom network handlers rows to identify an owner; no mod is declared guilty from the supplied native markers.
