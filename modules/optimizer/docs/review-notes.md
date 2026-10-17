# Engineering review notes

This was a targeted review of the rebuilt 0.1.0 preview, not an exhaustive audit or a production compatibility certification. The public Suite 1.2.43 package was not altered.

The review covered bounded profiler storage, ownership of retained references, hook cleanup, reported metric scope, benchmark evidence gates, local test-clone isolation, subprocess invocation, client input cleanup, and dashboard file access.

Corrections made during review include:

- Compatibility safe mode suppresses optional detailed hooks. A failed or conflicting timing group stays disabled rather than allowing a later hook to hide the failure.
- Fixed timing descriptors are cached. Callback keys retain bounded method metadata rather than handler, entity, player, block, or terrain instances.
- Missing measurements remain unavailable. Tick work excludes the deliberate engine sleep, and effective TPS uses completed ticks divided by elapsed wall time.
- Comparison code revalidates profiler evidence and rejects synthetic, idle, unauthenticated, incomplete, or mismatched workloads. Input events remain attempted actions until independently confirmed.
- Client input is released on errors and interruption. Loss, replacement, truncation, or expiry of server session evidence prevents a successful evidence result.
- Local smoke checks reject output inside their source snapshot, avoiding recursive self-copy. Benchmark execution uses fresh copies with explicit clone markers and matching recorded input fingerprints.
- Subprocess calls use argument arrays without adding a shell. Dashboard routes select only bundled static assets and one operator-selected report file; URL paths do not become arbitrary filesystem paths.

The test suites exercise these boundaries with synthetic fixtures and dry runs. Their passing results do not establish correct combat, inventory transactions, mod compatibility, sustained memory behavior, or multiplayer performance. See the delivered validation report for exact executed test counts and local server results.

Authenticated graphical clients tested in this development workspace: **0**. Official-client binaries and account sessions must be supplied by the test operator. A window PID does not independently prove an executable's provenance. Session presence does not prove gameplay success.

No engine gameplay optimization has been validated against a representative multiplayer workload in this preview. Entity/AI scheduling, world generation, chunk I/O, packet batching and incremental autosave remain research targets. The preview does not replace official assemblies or alter authoritative simulation to obtain better numbers.
