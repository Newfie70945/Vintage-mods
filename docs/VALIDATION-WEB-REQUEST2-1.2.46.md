# WebRequest2 — Suite 1.2.46

Build: `ms11-web-request2`. Retains WebRequest1. Companion versions are unchanged.

## Changes

The global status strip requests `/api/header` instead of `/api/dashboard`. A tiny health-state reader obtains the latest sample and already-calculated health score without accessing histories, engine profiler data or diagnostic analysis. Header snapshots coalesce for two seconds. The score tooltip shows the calculation timestamp; the header does not force recalculation, so it can show an older score or no score until diagnosis has run.

Dashboard, header and Server Status cache entries opt into a serialized snapshot wrapper. The first HTTP worker prepares JSON, concurrent readers share it, and later reads reuse the same bytes. The wrapper lives with its snapshot, so replacement, eviction and listener shutdown release cached bytes naturally. It retains at most 256 KiB per snapshot; bigger bodies are returned normally without retention. The existing object cache has 128 entries, so its retained serialized bodies cannot exceed 32 MiB; in-flight responses can temporarily retain additional buffers. No browser cache or authorization bypass is introduced. Game-thread state capture remains on the game thread.

Request diagnostics now record completion time, JSON preparation wall time, byte reuse and up to eight actual callback intervals with measured wall duration and coincident process GC pause deltas. JSON wall time includes contention while waiting for a snapshot's serialization lock; it is not CPU time. It is nullable for old records, whose missing values cannot be reconstructed.

Incidents retain at most eight matching completed requests, selecting by request interval and ranking measured overlapping callbacks first. Recent archives are enriched when requests finish; matching reads the existing bounded 500-request ring once per maintenance update with newly completed requests. It does not add a periodic sampler. Request rows appear in both spike views and Copy Summary.

Cause scoring uses the largest overlapping callback wall duration minus coincident GC, not queue wait, whole-request duration, or an old aggregate without callback intervals. These measurements can still include waits and overlap other measurements. They do not prove exclusive causation or name the allocation source. Requests still executing after timeout may be absent. Existing timed-out queued GET abandonment and shared snapshot coalescing remain intact; this pass does not add disconnect detection or cancel a refresh needed by other readers.

## Verification

- Official 1.22.7 SDK build: zero warnings/errors.
- Cache/runtime tests: cold and stale coalescing, invalidation races, failure retry, paused admission, listener restart, shared serialization, replacement identity, oversized-body bypass and serialization failure recovery.
- Focused allocation comparison: 1,000 warm cached 25 KB body reads allocated 0 bytes in the lookup loop, versus 25,040,000 bytes from serializing repeatedly. This excludes request/transport overhead and cold preparation.
- Matching tests: spanning requests, unrelated callbacks, GC discount, legacy aggregate-only data, queue-only requests and eight-row bounds.
- Shipping-assembly attribution regression: 25 checks plus compact header without history/API objects.
- Chromium: lightweight endpoint, counters, score timestamp, filtered/escaped web evidence, copied JSON/cache metrics, existing incident evidence/mobile and Performance Center navigation.
- Official-server HTTP integration: unauthenticated header rejected, owner login, correct unwrapped JSON bodies, 32 header requests through 16 client workers, shared cache, Server Status and Tick Debugging status. [Sanitized results](validation/web-request2-official-server-1.2.46.json). On the empty local fixture, header body was 657 bytes, dashboard 2,586 bytes and Server Status 7,090 bytes; maximum concurrent header-request duration was 14.25 ms. These are smoke-test measurements, not production TPS/scaling claims.

## Install and inspect

Stop the server, replace the previous Suite ZIP, restart and refresh the browser. Keep one Suite build installed. In Performance Center click a new spike, then inspect **Web requests overlapping this spike** or Copy Summary. Compare actual callback work with waits and GC. Do not add the timing columns together.

The larger proposed roadmap (global background-work budgeting, incremental updates, deeper storage/chunk instrumentation and broad allocation attribution) remains future work requiring separate profiling and compatibility validation.
