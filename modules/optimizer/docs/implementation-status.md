# Implementation scope

This is a new source project built after the previous development workspace was
lost. It is not a byte-for-byte restoration of NOS 0.6.0 or its tests. The latest
provided public Suite baseline is 1.2.43; this project does not change that ZIP.

The supplied development prompt requires measuring real multiplayer workloads
before retaining engine optimizations. This preview implements the measurement
and test-tool foundation. It does not claim a higher sustainable player count.

The official 1.22.7 dedicated-server distribution is available for compilation
and isolated local validation. Official graphical clients, authenticated test
accounts, a representative production modpack and a production world clone are
not available in this workspace. No authenticated multiplayer workload can be
claimed here. The stress tester automates genuine official client windows on an
owner-controlled test machine; it does not synthesize server-side players.

Gameplay-changing AI scheduling, activation ranges, repulsion, packet dropping,
chunk-priority changes, incremental autosave and world-generation replacement
are not implemented or presented as complete. Implementing them before workload
profiling would contradict the supplied development order. Configuration and
diagnostics distinguish available instrumentation from unimplemented areas.

Further phases require recorded player workloads, a validated bottleneck,
an independently switchable minimal change and repeatable correctness and
performance comparisons. Short local idle checks establish startup, profiler
operation and save/reload behavior only.
