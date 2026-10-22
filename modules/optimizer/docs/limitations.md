# Boundaries and production limits

- Only the inspected official 1.22.7 API/server DLL hashes are accepted for engine replacements. Another official platform/build may have a different hash and be blocked until inspected.
- Do not stack this preview with Stratum, Tungsten or Synergy without a dedicated compatibility pass. Existing foreign Harmony owners on a target cause an independent module conflict.
- Profile stop removes profiler hooks; engine modules continue until disabled/restarted or disposed.
- Exact array contents, ordering and caller ownership are preserved; output result arrays still allocate. Temporary list capacity is bounded and lists are cleared after callbacks, including exceptions.
- No serialization format, game tick rate, AI decision cadence, entity collision rule, world-generation rule or save scheduling change is made.
- A method already in flight can finish while a late conflict is being disabled. Monitor cadence is one second; arbitrary third-party hot-patching cannot be made race-free by polling.
- Query callback failures are propagated once. They can conservatively cause the query module to stand down at the next monitor tick.
- Component fixture and byte-copy microbenchmarks establish narrow method behavior and costs. They do not simulate authenticated players, sockets, chunk delivery, AI, inventories, block edits or a modded production workload.
- Headless save/load checks do not establish graphical client correctness or modpack-wide compatibility. A staging rollout and matched workload are still needed.
- The supplied external Stratum capture is one run on different hardware/runtime. Its sample counts are not method invocation counts and cannot establish NOS-versus-Stratum performance.

## 0.7.0 scope

The optional main-thread path assistance module changes path scheduling. It is off by default. Its grace period starts when the same queue head is first observed; it is not a per-task enqueue age. A single A* search is non-preemptible and may exceed the soft budget. Different creature types are passed to stock A*. It does not implement walkability memoization, closed-region path caching, food-search jitter, shared shape hydration, entity sleeping or reduced AI tick cadence.

GC pressure is addressed through allocation reduction and explicit stopped-report reference release. NOS does not force collections, compact the heap, change GC latency modes, impose heap limits or guarantee immediate RSS reduction. Component allocation savings and real-terrain correctness are not multiplayer performance claims.
