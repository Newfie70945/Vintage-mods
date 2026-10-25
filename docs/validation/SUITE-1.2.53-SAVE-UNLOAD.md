# Suite 1.2.53 — save persistence and stall attribution

## Changes

- Reuse typed SQLite parameters for remembered-player persistence instead of recreating seventeen parameter objects per player.
- Skip SQLite row updates when all persisted player fields are unchanged. The existing synchronous flush and transaction semantics remain in place; no schema migration or in-memory dirty cache is introduced.
- Add native tick-profiler substeps for Suite world-save inventory capture, storage flushing, despawn observers and despawn audit queuing. These scopes appear when the native tick profiler is enabled.
- Retain bounded timings for Suite save/despawn operations taking at least 1 ms. These are explicitly labelled slow-operation samples, not averages of every invocation.

## Validation

The production persistence methods were compared against commit 8a0c65b using 5,000 records and in-memory SQLite. All sixteen non-key persisted fields, null normalization, unchanged writes, forced transaction failure/rollback and successful retry passed. Repeated unchanged flushes changed 0 rows versus 5,000 previously. Allocations were 11,405,056 versus 16,843,072 bytes (32.3% lower); measured times overlapped and are not a live-server autosave performance claim.

Release build on official Vintage Story 1.22.7: zero warnings and errors. Source consistency and whitespace checks passed. See the accompanying TEST-RESULTS for disposable-server integration results.

## What the supplied captures establish

Two autosave ticks spent about 920–995 ms in The BASICs chat-history save, 536–635 ms in native save preparation, and 35–37 ms in Suite admin saving. This patch reduces redundant Suite persistence work; it does not fix the separate The BASICs save handler or native preparation.

Several unload stalls occur near multiples of 150 ms, including an unload section taking 1,404 ms of a 1,545 ms tick. Candidate scanning was approximately 1 ms. Animal labels alone cannot identify the responsible mod: global despawn subscribers execute between native marks and can be charged to another label. The Suite substeps help test its contribution; they do not prove which third-party handler blocks.

No chunk lifecycle reordering, callback skipping, forced GC, save disabling or animal behavior throttling was added. NOS remains 0.20.11-preview, with its previous experimental settings unchanged. A heavily modded production performance improvement has not been established by these component and empty-server tests.
