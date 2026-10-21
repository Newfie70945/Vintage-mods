# Suite 1.2.45 StorageSave1 audit

Build: `newfies-server-essentials-suite-1.2.45-ms11-storage-save1`. Retains Performance1 startup/safety improvements. Companion versions and owner recording/retention settings are unchanged.

## Storage

The viewer already separates known table/index usage, WAL, free pages and backup files using manual bounded analysis. The new expandable guide explains exact controls, example values, purge/compaction behavior and lost recovery detail. Per-table recommendations now use exact setting names and explicitly warn that take/put evidence alone may not reconstruct container contents. See [storage guide](DATABASE-STORAGE-GUIDE.md).

Automatic retention previously ordered expired-row selection by ID. When expired history had high IDs (e.g. a late import), SQLite could scan retained low-ID history. Selection now orders by the already-indexed timestamp. The cutoff, batch limits, table coverage, hourly scheduling, writer gate and yield behavior are unchanged. Rows still expire only according to the owner's existing RetentionDays policy; no extra deletion or compaction is introduced.

A 50,000-retained + 1,000-expired-row fixture measured SQLite VM steps:

| Table | Previous order | Timestamp order | Per-pass deletion cap |
| --- | ---: | ---: | ---: |
| events | 160,026 | 9,525 | 500 |
| snapshots | 152,026 | 1,925 | 100 |
| entity_activity | 160,026 | 9,525 | 500 |

All retained rows survived. This demonstrates work reduction for late-imported/out-of-order expired history, not a universal speedup or smaller existing database. Retention still yields to gameplay writes; large backlogs can remain.

## Save/lag correlation

The Operations Center already measured world-save windows, but lag incident grouping and smart diagnosis did not use those measurements. The lightweight profiler now independently subscribes to the official GameWorldSave event while diagnostics are enabled. A post-save main-thread callback measures the observed window; no continuous engine profiler capture is enabled for this observation.

Match that measured window against the actual preceding one-second sampler interval plus measured delay, rather than comparing counts or using a broad nearby-time guess. Ignore sub-150ms save windows and healthy intervals. The bounded window queue retains at most 128 value records; callbacks revisit at most 100 retained incidents so callback ordering cannot silently lose an association. Shutdown unsubscribes, clears the queue and rejects late callbacks. No save window creates a fabricated lag event.

Matched incidents carry save timestamp/window evidence, the selected-range event count reports save-correlated samples, and recent grouped/smart diagnosis includes World save (correlated). Smart health diagnosis still uses its existing recent five-minute window; the raw event list can cover the selected one-hour range. A world-save event may be automatic or manual; the public event does not identify which initiated it.

**Six saves and six spikes do not prove causation.** Save windows include normal callback scheduling and other main-thread work, not pure disk write duration. GC, CPU and logger pressure remain visible alongside save evidence. This update observes future events while diagnostics are enabled; it cannot establish the cause of old events without their timing evidence.

## Checks

- Official Vintage Story 1.22.7 / .NET 10 release build: zero warnings/errors.
- Eleven pure correlation checks cover overlap, unrelated/future windows, equal unrelated save/spike counts, fast saves, healthy intervals, strongest overlap, boundaries and invalid delays.
- Production retention SQL tests verify VM work, retained-row preservation and unchanged batch caps for all three tables.
- Browser checks cover the mobile guide, exact controls, recovery warnings, no automatic write requests and existing performance-timeline behavior.
- Disposable official-server integration deliberately adds a 1,800ms pause inside one real GameWorldSave event to exercise detection. It is a functional test, **not** a benchmark or production workload. OperationsCenter and WebDashboard are disabled to verify that profiler correlation is independent.
