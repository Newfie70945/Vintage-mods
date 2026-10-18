# NOS 0.5.0 — generation requests and completed save snapshots

Both modules default on and operate with profiling stopped. Suite 1.2.48 exposes their independent switches on Optimizations. Official 1.22.7 API/server/Essentials hashes, dependent-method conflict checks, live conflict monitoring, safe mode and teardown guards apply.

## Generation request allocation reduction

The official `ChunkServerThread.EnsureMinimumWorldgenPassAt` constructs a capturing factory even when the indexed request already exists. NOS uses the dictionary's state-argument overload with a cached static factory. It preserves the ready-map early return, request constructor inputs, queue insertion, pass escalation, creation-time priority and completion result. The original method body remains available behind the disabled branch.

Five trials called the actual official target through a delegate, comparing the unpatched method against the enabled Harmony patch, with 20,000 warmup and 200,000 measured calls per mode and tiered compilation disabled. The same populated queue was used for repeated existing-request lookups. Stock median: **25.50 ns, 96 B/call**. Optimized median: **23.13 ns, 0 B/call**. Allocation savings were consistent; one optimized timing trial was slower. Timings are a small synthetic component result, not chunk-generation throughput or TPS evidence. New request creation still allocates its required request object and collections.

[Raw component trials](research/components-0.5.0/worldstorage-measurements.json).

## Save snapshot reference release

Stock `SaveGameWorld(true)` copies all loaded chunks into `chunksCopy`. The background worker saves from that dictionary but leaves it populated after success. If those chunks subsequently unload, this stale dictionary can retain their object graphs until another save populates the snapshot.

NOS clears the dictionary immediately after the worker's successful save-completion flag store, while still inside its existing save lock. Loaded chunks remain held by the normal world index. The dictionary capacity is reusable. The change does not force chunk unloading or GC, change save frequency, serialize on the main thread, alter dirty flags, or change save format, transaction boundaries or persistence order.

On exceptions, stock does not reach the completion flag store, so NOS does not clear the snapshot. Original exception propagation, pending flag and retry inputs remain. The ordinary synchronous shutdown save is unchanged. This module fixes a retention source; it does not promise lower disk latency or a particular number of megabytes reclaimed.

## Validation boundaries

41 component assertions cover actual official generation request behavior, queue order and request metadata, disabled stock behavior, failed save/retry preservation, stock lock release on exceptions, completed snapshot weak-reference reclamation and rejection of unexpected completion IL. The separate engine registry suite covers independent switches, verified activation, foreign patches, late conflicts, disposal and safety gates. Browser tests cover new controls, persistence requests, draft preservation, role restrictions and older NOS versions lacking these settings.

Running-server validation uses isolated copies of a local test save, the exact packaged Suite/NOS ZIPs and the official 1.22.7 runtime with authentication verification enabled. It compares completed saves with snapshot release enabled and disabled, tests Suite settings reloads, checks the resulting SQLite file, and reopens the saved world. See [running-server evidence](research/server-storage-validation-0.5.0.json). The stock-mode completed snapshot retained 40 entries; enabled-mode snapshots contained zero after both saves. The reopened-world save released 424 references and again retained zero snapshot entries. Both database integrity checks returned `ok`, with clean server exits and no server errors. No authenticated players or production modpack workload were used.

## Further storage work

Incremental autosave, parallel world generation, compression changes, chunk I/O batching and changes to thread counts remain unimplemented. They require workload evidence and durability/compatibility tests beyond these narrowly scoped fixes. Existing save writes and transactions are preserved in this release.

## Reproducing the storage checks

Build `tests/SuiteIntegrationProbe` against the official game and install its test-only DLL alongside the packaged Suite and NOS on an isolated save copy. It is not included in production mod ZIPs. Run `/nosbridgetest`, then `/nosgenerate` and wait for `NOS_WORLDGEN_PASS`. Run `/autosavenow`, wait for off-thread save completion, then `/nosstoragecheck`. Toggle release off with `/nosstoragetoggle`, save again and run `/nosstoragestock`; toggle on, save and repeat `/nosstoragecheck`. Stop, reopen that save, load the test columns again, save and repeat the enabled check. These commands require `controlserver` and belong only on the disposable test server.

## Repeated workload measurements

The later [nine-run generation/save test](performance-tests-0.5.0.md) confirms the reference-retention fix but does not establish a generation or save throughput improvement. Default-NOS first-save and tick-tail timings were slower in the main sample; incidental entity populations varied, preventing causal attribution. Peak managed heap was similar.
