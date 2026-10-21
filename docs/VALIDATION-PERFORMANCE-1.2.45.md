# Suite 1.2.45 — storage startup performance and safety audit

Build: `newfies-server-essentials-suite-1.2.45-ms11-performance1`. Companions retain their existing versions.

## Findings and changes

The supplied production startup log had approximately 232 seconds between Player List readiness and BlockLogger readiness, within a roughly 421-second startup. That interval includes storage initialization; the old log did not separate validation, legacy migration and schema work. It does not prove validation caused the entire delay.

- Removed the mandatory full-database `PRAGMA quick_check` from normal startup. Existing databases get a read-only schema/table accessibility probe, avoiding a full scan of large logging databases. This is **not** a full integrity certification; corruption in unvisited pages can remain undetected.
- Added `FullDatabaseIntegrityCheckOnStartup` (default `false`) and a webpanel checkbox under BlockLogger maintenance. Enabling it applies next startup. The manual full quick-check remains available in Suite Health.
- Timed database validation, legacy database location, schema/index work and backup retention separately. Logs and maintenance status expose actual startup mode and stage durations, so production follow-up can identify the remaining bottleneck.
- Grouped existing schema/index changes in one transaction. Successful startup retains the same schema/indexes; failures roll back the migration instead of leaving a partial schema change.
- Automatic logger database backups wait for RunGame and at least two minutes after storage initialization, then still obey the existing interval, pending-work and quiet-server checks. Explicit manual backups and world-migration backup policies are unchanged. This defers I/O; it does not reduce the total copy size.
- Manual integrity scans now allow one scan per logger, use a read-only connection, interrupt native SQLite work on shutdown/deadline, and reject duplicate work instead of creating overlapping scans. The internal work deadline is ten minutes; an HTTP request can time out earlier while the single background scan finishes.
- Startup validation errors no longer automatically rename the live database/WAL/SHM or restore an older backup. Busy/locked, permission and I/O failures are not sufficient evidence of corruption. Logger startup failure is reported and leaves other Suite services available; logger-backed commands/history are unavailable for that session.
- Previously quarantined `blocklogger-corrupt-*` originals are no longer deleted by healthy-backup retention. Review/remove those files manually after recovery. Healthy-backup rotation remains unchanged.

## Validation

- Release build against official Vintage Story **1.22.7 / .NET 10**: zero warnings/errors.
- 16 database component checks: existing record preservation, idempotent schema/index creation, legacy schema upgrade, transactional rollback, exclusive lock handling, invalid-file preservation, read-only missing-file behavior and automatic-backup warmup boundaries.
- Official-server integration uses only a copied disposable world with a loopback listener and no authenticated game clients. Tests light/full startup, saved next-start setting, manual integrity maintenance, invalid logger database preservation and availability of other Suite/webpanel services.
- Browser checks cover maintenance setting save, actual startup-mode reporting, stage timings, escaped error text and mobile width. Existing migration-page regression checks are retained.
- Existing recovery performance checks exercise native SQLite interruption/cancellation and reader admission.

Raw measurement and official-server outcome summary: [performance-1.2.45.json](validation/performance-1.2.45.json).

## Component benchmark

Synthetic database: 100,000 events, approximately **410.7 MiB**, 40 synthetic player identities, indexed schema and 2 KiB event metadata. Six iterations alternate full/lightweight order on the same database. Local filesystem/cache effects apply; this is not a cold-disk production benchmark.

| Probe | Median elapsed | Median process CPU | Median managed allocation |
| --- | ---: | ---: | ---: |
| Full startup quick-check | 224.456 ms | 244.527 ms | 6,256 B |
| Lightweight accessibility probe | 0.554 ms | 0.775 ms | 5,728 B |

Benchmark runner links the production probe/schema source with Microsoft.Data.Sqlite 10.0.10 and native SQLite 3.53.3. Official-server integration uses the game's own libraries. Results measure **validation only**, not total startup, sustained TPS, resident memory or full-modpack behavior. No production performance percentage is claimed.

Reproduce:

```sh
dotnet run --project modules/suite/tests/StorageStartupChecks/StorageStartupChecks.csproj -c Release
dotnet run --project modules/suite/tests/StorageStartupChecks/StorageStartupChecks.csproj -c Release -- --benchmark /fresh/output/directory 100000
# After building the Suite, against a stopped disposable baseline only:
NSS_BATCH_TEST_BASELINE=/path/to/disposable/data VINTAGE_STORY=/path/to/official/game DOTNET_ROOT=/path/to/dotnet python3 modules/suite/tests/StorageStartupChecks/server_startup.py
```

## Remaining areas

Large legacy database copying and first-time index creation can still block startup; the new timings reveal their cost before attempting asynchronous schema changes. Automatic backups are still full SQLite copies and can compete for disk bandwidth after warmup. Retention/VACUUM, large history queries, map export and high player/entity workloads need representative production profiling before further invasive changes. Existing writer batching, reusable prepared statements and bounded event queues were retained. This pass does not establish or resolve a production memory leak, and it does not change entity AI, world generation or network simulation.
