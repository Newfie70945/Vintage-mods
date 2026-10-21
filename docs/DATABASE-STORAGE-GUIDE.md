# Reducing Suite database storage

Open **Database Storage → Analyze Storage**. Identify whether the space is in live table data, indexes, free pages, WAL, backups or another Suite-owned database. Detailed scans are manual and can be partial; file totals alone cannot explain a 25 GB database. The viewer does not analyze the game's world-save database.

No setting is changed automatically by this guide. Use Block Logging → Recording and Maintenance; less history means fewer recovery options.

| Area | Settings to review | Tradeoff |
| --- | --- | --- |
| Interaction events | `LogBlockUse`, entity spawn/despawn switches, `IgnoredBlockCodePrefixes` and `IgnoredEntityCodePrefixes` | Stops future matching records. Keep break/place and take/put evidence when investigations require it. Disabling logging does not delete existing rows. |
| Container snapshots | `LogContainerSnapshots`, `SnapshotOnSlotChange`, `SnapshotDebounceMs` | Keep full snapshots when inventory reconstruction matters. Take/put logs alone may not be enough. Increasing debounce from 2000 to 5000 ms can reduce repeated snapshots but loses intermediate states. |
| Entity activity | `AggregateEntitySpawnDespawn`, `AdaptiveEntityAggregation`, `EntityAggregateWindowMs` | Groups spawn/despawn activity, reducing individual event detail. A longer window (e.g. 10000 → 30000 ms) may help; workload and flush behavior affect the result. |
| Old history | `RetentionDays`, default 180 | Consider 30–60 only if that meets your investigation/recovery needs. **0 disables automatic expiry.** Existing small hourly batches prioritize gameplay writes; a large backlog may need a reviewed manual category purge. |
| Healthy logger backups | `DatabaseBackupRetentionCount`, default 2; `AutoDatabaseBackupIntervalHours`, default 72 | Fewer retained copies reduce disk usage; a longer interval reduces copying frequency. Interval 0 disables automatic backups but does not remove existing files. Keep a known-good recovery copy elsewhere. |
| Admin inventory snapshots | Avoid unnecessary repeated manual snapshots | These are full-fidelity item stacks used for administration rollback. Existing per-player caps apply; don't manually delete linked snapshot tables. |

After a purge, SQLite normally **reuses free pages** rather than shrinking the file immediately. Check reclaimable space in Analyze Storage. Optional VACUUM can reclaim physical disk space during maintenance; it can be expensive and needs spare disk capacity. Required indexes keep player/block history queries fast. A large WAL can reflect pending checkpoints or long-running readers: **never delete an active `-wal` or `-shm` file by hand**.

World/migration backups, retained exports and quarantined `blocklogger-corrupt-*` originals are separate from healthy logger backup rotation. Review these separately, preferably offline, and preserve useful recovery copies. Merely disabling an automatic schedule does not erase old files.

Suggested order: measure → reduce unwanted future recording → choose a suitable retention window → review/export history before a category purge → compact only if physical disk reclamation is necessary. Do not turn off container snapshots simply to save space if restoring bookshelves/chests remains your priority.
