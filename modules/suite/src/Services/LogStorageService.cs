using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Models;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

// Owns the bounded queues and background SQLite/query workers.
public sealed class LogStorageService : IDisposable
{
    private sealed record EventRequest(
        int X, int Y, int Z, int Dimension, int Radius, int MaxCount, int Offset, string? Text, string? Tag,
        bool EntityOnly, bool PlayerOnly, string? Player,
        long EnqueuedUnixMilliseconds,
        TaskCompletionSource<List<LogEventRecord>> Completion)
    {
        public bool ExactUid { get; init; }
    }

    private sealed record SnapshotRequest(
        int X, int Y, int Z, int Dimension, int Radius, int MaxCount, string? Text,
        long EnqueuedUnixMilliseconds,
        TaskCompletionSource<List<ContainerSnapshotRecord>> Completion);

    private readonly ICoreServerAPI api;
    private readonly string dbPath;
    private readonly string eventLogDir;
    private readonly string snapshotLogDir;
    private readonly string backupsPath;
    private long lastAutoBackupCheckUnixMilliseconds;
    private long lastBackupUnixMilliseconds;
    private long lastDatabaseGrowthCheckUnixMilliseconds;
    private long lastDatabaseObservedBytes;
    private long lastDatabaseGrowthWarningUnixMilliseconds;
    private int backupInProgress;
    private int retentionInProgress;
    private int recoveryWriterInFlight;
    private readonly RecoveryQueryRunner recoveryQueries = new();
    private volatile int autoBackupIntervalHours;
    private volatile int backupRetentionCount;
    private readonly ConcurrentQueue<QueuedEventPayload> criticalEventQueue = new();
    private readonly ConcurrentQueue<QueuedEntityEventPayload> criticalEntityQueue = new();
    private readonly ConcurrentQueue<QueuedEntityEventPayload> bulkEntityQueue = new();
    // v1.5.15 emergency reserve: callbacks remain non-blocking, but short writer/SQLite
    // stalls no longer immediately turn normal queue saturation into lost audit rows.
    private readonly ConcurrentQueue<QueuedEventPayload> overflowEventQueue = new();
    private readonly ConcurrentQueue<QueuedEntityEventPayload> overflowEntityQueue = new();
    private readonly ConcurrentQueue<EntityActivityAggregate> entityAggregateQueue = new();
    private readonly ConcurrentQueue<ContainerSnapshotRecord> snapshotQueue = new();
    private readonly SemaphoreSlim eventSignal = new(0);
    private readonly BlockingCollection<EventRequest> queryQueue = new(new ConcurrentQueue<EventRequest>(), 64);
    private readonly BlockingCollection<SnapshotRequest> snapshotQueryQueue = new(new ConcurrentQueue<SnapshotRequest>(), 32);
    private readonly CancellationTokenSource cts = new();
    private int disposeState;
    // Serialize SQLite writes across live traffic, migration, and maintenance.
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Task eventWriterTask;
    private readonly Task queryTask;
    private readonly Task snapshotQueryTask;
    private readonly Task migrationTask;
    private volatile int maxQueue;
    private volatile int emergencyOverflowCapacity;
    private volatile int flushIntervalMs;
    private readonly string currentWorldId;
    private long queuedEvents;
    private long queuedCriticalEvents;
    private long queuedBulkEntityEvents;
    private long queuedOverflowEvents;
    private long peakQueuedOverflowEvents;
    private long overflowAcceptedEvents;
    private long overflowRejectedEvents;
    private long queuedSnapshots;
    private long droppedEvents;
    private long droppedSnapshots;
    private long lastDropWarningTicks;
    private volatile bool migrationComplete;
    private long writtenEvents;
    private long writtenEntityEvents;
    private long writtenEntityAggregates;
    private long queuedEntityAggregates;
    private long peakQueuedEntityAggregates;
    private long droppedEntityAggregates;
    private long droppedEntityEvents;
    private long writtenSnapshots;
    private long peakPendingEvents;
    private long lastBatchRows;
    private long lastBatchEntityRows;
    private long lastBatchElapsedMs;
    private long lastSuccessfulWriteUnixMilliseconds;
    private long lastStorageErrorUnixMilliseconds;
    private string lastStorageError = "";
    private long writerBatchCount;
    private long writerBatchTotalMs;
    private long writerBatchMaxMs;
    private long busyOrLockedErrors;
    private long lastIntegrityCheckUnixMilliseconds;
    private long lastIntegrityCheckMilliseconds;
    private string lastIntegrityResult = "Not run";
    private long queryCount;
    private long queryTotalMs;
    private long queryMaxMs;
    private long lastQueryMs;
    private string lastQueryKind = "";
    private long slowQueryCount;
    private const int MaxRecentStorageMetrics = 512;
    private readonly ConcurrentQueue<StorageBatchMetric> recentBatchMetrics = new();
    private readonly ConcurrentQueue<StorageQueryMetric> recentQueryMetrics = new();
    private int recentBatchMetricCount;
    private int recentQueryMetricCount;

    public LogStorageService(
        ICoreServerAPI api,
        int maxQueue,
        int emergencyOverflowCapacity,
        int flushIntervalMs = 750,
        bool autoMigrateLegacyLogs = false,
        int autoBackupIntervalHours = 72,
        int backupRetentionCount = 2,
        bool fullIntegrityCheckOnStartup = false)
    {
        this.api = api;
        this.maxQueue = Math.Max(100, maxQueue);
        this.emergencyOverflowCapacity = Math.Max(1000, emergencyOverflowCapacity);
        this.flushIntervalMs = Math.Clamp(flushIntervalMs, 100, 5000);
        this.autoBackupIntervalHours = Math.Clamp(autoBackupIntervalHours, 0, 24 * 365);
        this.backupRetentionCount = Math.Clamp(backupRetentionCount, 1, 20);
        currentWorldId = api.World.SavegameIdentifier ?? "unknownworld";

        // v1.3 Storage v2: persistent databases live under the actual Vintage Story data directory,
        // isolated per savegame. Keep the old install-directory JSONL folders only as migration sources.
        var legacyRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "BlockLogger"));
        eventLogDir = Path.Combine(legacyRoot, "logs");
        snapshotLogDir = Path.Combine(legacyRoot, "containerlogs");
        var root = SuiteDataPaths.GetWorldRoot(api);
        backupsPath = Path.Combine(root, "backups");
        Directory.CreateDirectory(backupsPath);
        dbPath = Path.Combine(root, "blocklogger.db");
        StartupValidationMode=fullIntegrityCheckOnStartup?"Full integrity scan":"Lightweight read-only probe";
        MeasureStartup("database validation",()=>ProbeStartupDatabase(fullIntegrityCheckOnStartup));
        MeasureStartup("legacy database location",()=>MigrateLegacyDatabaseIfNeeded(Path.Combine(legacyRoot,"blocklogger.db"),dbPath));
        MeasureStartup("schema and indexes",InitializeDatabase);
        MeasureStartup("backup retention",TrimDatabaseBackups);
        StartupMilliseconds = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(startupMilliseconds);
        storageStarted = Stopwatch.GetTimestamp();
        api.Logger.Notification("[BlockLogger/Startup] Validation={0}. Automatic backups wait until RunGame and at least two minutes after storage initialization; existing quiet-workload checks also apply.",StartupValidationMode);

        migrationTask = autoMigrateLegacyLogs
            ? StartDedicatedWorker(() => MigrateLegacyJsonlSafe(cts.Token), cts.Token)
            : Task.CompletedTask;
        if (!autoMigrateLegacyLogs) migrationComplete = true;
        eventWriterTask = StartDedicatedWorker(() => EventWriterLoop(cts.Token), cts.Token);
        // These delegates must run their finally blocks even if shutdown wins scheduling.
        queryTask = StartDedicatedWorker(() => QueryLoop(cts.Token), CancellationToken.None);
        snapshotQueryTask = StartDedicatedWorker(() => SnapshotQueryLoop(cts.Token), CancellationToken.None);
    }

    private static Task StartDedicatedWorker(Action action, CancellationToken token)
    {
        return Task.Factory.StartNew(() =>
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            action();
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public bool IsReady => migrationComplete;
    public long PendingEvents => Interlocked.Read(ref queuedEvents) + Interlocked.Read(ref queuedOverflowEvents);
    public long PendingSnapshots => Interlocked.Read(ref queuedSnapshots);
    public long PendingCriticalEvents => Interlocked.Read(ref queuedCriticalEvents);
    public long PendingBulkEntityEvents => Interlocked.Read(ref queuedBulkEntityEvents);
    public long PendingOverflowEvents => Interlocked.Read(ref queuedOverflowEvents);
    public long PeakPendingOverflowEvents => Interlocked.Read(ref peakQueuedOverflowEvents);
    public long OverflowAcceptedEvents => Interlocked.Read(ref overflowAcceptedEvents);
    public long OverflowRejectedEvents => Interlocked.Read(ref overflowRejectedEvents);
    public int EmergencyOverflowCapacity => emergencyOverflowCapacity;
    public long DroppedEvents => Interlocked.Read(ref droppedEvents);
    public long DroppedSnapshots => Interlocked.Read(ref droppedSnapshots);
    public long WrittenEvents => Interlocked.Read(ref writtenEvents);
    public long WrittenEntityEvents => Interlocked.Read(ref writtenEntityEvents);
    public long WrittenEntityAggregates => Interlocked.Read(ref writtenEntityAggregates);
    public long PendingEntityAggregates => Interlocked.Read(ref queuedEntityAggregates);
    public long PeakPendingEntityAggregates => Interlocked.Read(ref peakQueuedEntityAggregates);
    public long DroppedEntityAggregates => Interlocked.Read(ref droppedEntityAggregates);
    public int QueueCapacity => maxQueue;
    public long DroppedEntityEvents => Interlocked.Read(ref droppedEntityEvents);
    public long WrittenSnapshots => Interlocked.Read(ref writtenSnapshots);
    public long PeakPendingEvents => Interlocked.Read(ref peakPendingEvents);
    public long LastBatchRows => Interlocked.Read(ref lastBatchRows);
    public long LastBatchEntityRows => Interlocked.Read(ref lastBatchEntityRows);
    public long LastBatchElapsedMs => Interlocked.Read(ref lastBatchElapsedMs);
    public long LastSuccessfulWriteUnixMilliseconds =>
        Interlocked.Read(ref lastSuccessfulWriteUnixMilliseconds);
    public long LastStorageErrorUnixMilliseconds =>
        Interlocked.Read(ref lastStorageErrorUnixMilliseconds);
    public string LastStorageError => Volatile.Read(ref lastStorageError) ?? "";
    public string DatabasePath => dbPath;
    public string LegacyEventLogPath => eventLogDir;
    public string LegacySnapshotLogPath => snapshotLogDir;

    private sealed record StorageBatchMetric(long UnixMilliseconds, int Rows, long Milliseconds);
    private sealed record StorageQueryMetric(long UnixMilliseconds, string Kind, long Milliseconds);

    private void MarkStorageSuccess()
    {
        Interlocked.Exchange(
            ref lastSuccessfulWriteUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void MarkStorageError(Exception ex)
    {
        if (ex is SqliteException sqlite && (sqlite.SqliteErrorCode == 5 || sqlite.SqliteErrorCode == 6))
            Interlocked.Increment(ref busyOrLockedErrors);

        Volatile.Write(
            ref lastStorageError,
            $"{ex.GetType().Name}: {ex.Message}");
        Interlocked.Exchange(
            ref lastStorageErrorUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void RecordQueryMetric(string kind, long milliseconds)
    {
        Interlocked.Increment(ref queryCount);
        Interlocked.Add(ref queryTotalMs, milliseconds);
        Interlocked.Exchange(ref lastQueryMs, milliseconds);
        Volatile.Write(ref lastQueryKind, kind);
        if (milliseconds >= 100) Interlocked.Increment(ref slowQueryCount);

        long previousMax;
        do
        {
            previousMax = Interlocked.Read(ref queryMaxMs);
            if (milliseconds <= previousMax) break;
        }
        while (Interlocked.CompareExchange(ref queryMaxMs, milliseconds, previousMax) != previousMax);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        recentQueryMetrics.Enqueue(new StorageQueryMetric(now, kind, milliseconds));
        Interlocked.Increment(ref recentQueryMetricCount);
        while ((Volatile.Read(ref recentQueryMetricCount) > MaxRecentStorageMetrics ||
                (recentQueryMetrics.TryPeek(out var old) && old.UnixMilliseconds < now - 5 * 60_000L)) &&
               recentQueryMetrics.TryDequeue(out _))
        {
            Interlocked.Decrement(ref recentQueryMetricCount);
        }
    }

    public BlockLogger.WebDashboard.DatabaseHealthSnapshot
        GetDatabaseHealth(int retentionDays)
    {
        long FileSize(string path)
        {
            try
            {
                return File.Exists(path)
                    ? new FileInfo(path).Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long recentCutoff = now - 5 * 60_000L;
        long minuteCutoff = now - 60_000L;
        var recent = recentBatchMetrics
            .ToArray()
            .Where(x => x.UnixMilliseconds >= recentCutoff)
            .ToList();
        var orderedDurations = recent
            .Select(x => (double)x.Milliseconds)
            .OrderBy(x => x)
            .ToList();
        double p95 = orderedDurations.Count == 0
            ? 0
            : orderedDurations[Math.Clamp((int)Math.Ceiling(orderedDurations.Count * 0.95) - 1, 0, orderedDurations.Count - 1)];
        long batches = Interlocked.Read(ref writerBatchCount);
        var recentQueries = recentQueryMetrics
            .ToArray()
            .Where(x => x.UnixMilliseconds >= recentCutoff)
            .Select(x => (double)x.Milliseconds)
            .OrderBy(x => x)
            .ToList();
        double queryP95 = recentQueries.Count == 0
            ? 0
            : recentQueries[Math.Clamp((int)Math.Ceiling(recentQueries.Count * 0.95) - 1, 0, recentQueries.Count - 1)];
        long queries = Interlocked.Read(ref queryCount);

        return new BlockLogger.WebDashboard.DatabaseHealthSnapshot
        {
            Available = true,
            Ready = IsReady,
            Path = dbPath,
            DatabaseBytes = FileSize(dbPath),
            WalBytes = FileSize(dbPath + "-wal"),
            ShmBytes = FileSize(dbPath + "-shm"),
            PendingEvents = PendingEvents,
            QueueCapacity = QueueCapacity,
            PendingOverflowEvents = PendingOverflowEvents,
            PeakOverflowEvents = PeakPendingOverflowEvents,
            OverflowCapacity = EmergencyOverflowCapacity,
            OverflowAcceptedEvents = OverflowAcceptedEvents,
            OverflowRejectedEvents = OverflowRejectedEvents,
            DroppedEvents = DroppedEvents,
            LastBatchRows = LastBatchRows,
            LastBatchMilliseconds = LastBatchElapsedMs,
            BatchCount = batches,
            AverageBatchMilliseconds = batches <= 0 ? 0 : Interlocked.Read(ref writerBatchTotalMs) / (double)batches,
            P95BatchMilliseconds = p95,
            MaxBatchMilliseconds = Interlocked.Read(ref writerBatchMaxMs),
            RowsPerMinute = recent.Where(x => x.UnixMilliseconds >= minuteCutoff).Sum(x => x.Rows),
            QueryQueueDepth = queryQueue.Count,
            SnapshotQueryQueueDepth = snapshotQueryQueue.Count,
            QueryCount = queries,
            AverageQueryMilliseconds = queries <= 0 ? 0 : Interlocked.Read(ref queryTotalMs) / (double)queries,
            P95QueryMilliseconds = queryP95,
            MaxQueryMilliseconds = Interlocked.Read(ref queryMaxMs),
            LastQueryMilliseconds = Interlocked.Read(ref lastQueryMs),
            LastQueryKind = Volatile.Read(ref lastQueryKind) ?? "",
            SlowQueryCount = Interlocked.Read(ref slowQueryCount),
            WriteGateBusy = writeGate.CurrentCount == 0,
            BusyOrLockedErrors = Interlocked.Read(ref busyOrLockedErrors),
            LastIntegrityCheckUnixMilliseconds = Interlocked.Read(ref lastIntegrityCheckUnixMilliseconds),
            LastIntegrityCheckMilliseconds = Interlocked.Read(ref lastIntegrityCheckMilliseconds),
            LastBackupUnixMilliseconds = Interlocked.Read(ref lastBackupUnixMilliseconds),
            BackupCount = Directory.Exists(backupsPath) ? Directory.EnumerateFiles(backupsPath, "blocklogger-*.db").Count(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)) : 0,
            BackupBytes = GetBackupBytes(includeCorrupt: false),
            BackupRetentionCount = backupRetentionCount,
            AutoBackupIntervalHours = autoBackupIntervalHours,
            BackupInProgress = Volatile.Read(ref backupInProgress) != 0,
            LastSuccessfulWriteUnixMilliseconds = LastSuccessfulWriteUnixMilliseconds,
            LastError = LastStorageError,
            LastErrorUnixMilliseconds = LastStorageErrorUnixMilliseconds,
            RetentionDays = retentionDays,
            Integrity = Volatile.Read(ref lastIntegrityResult) ?? "Not run",
            IntegrityCheckInProgress = Volatile.Read(ref integrityCheckRunning)!=0,
            StartupValidationMode=StartupValidationMode,
            StartupMilliseconds=StartupMilliseconds,
            Message = IsReady
                ? "SQLite writer/query services are initialized."
                : "SQLite is still completing legacy migration/index work."
        };
    }

    public async Task<BlockLogger.WebDashboard.DatabaseHealthSnapshot> CheckDatabaseIntegrityAsync(int retentionDays)
    {
        var health = GetDatabaseHealth(retentionDays);
        if (Interlocked.CompareExchange(ref integrityCheckRunning, 1, 0) != 0)
        {
            health.IntegrityCheckInProgress = true;
            health.Message = "An integrity scan is already running; no additional scan started.";
            return health;
        }
        var watch = Stopwatch.StartNew();
        try
        {
            string result = await integrityQueries.RunAsync(OpenReadOnlyConnection, (conn, token) =>
            {
                using var command = conn.CreateCommand();
                command.CommandText = "PRAGMA quick_check(1);";
                return Convert.ToString(command.ExecuteScalar()) ?? "unknown";
            }, cts.Token, CancellationToken.None, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            Volatile.Write(ref lastIntegrityResult, result);
            health.Integrity = result;
            health.Message = result.Equals("ok", StringComparison.OrdinalIgnoreCase)
                ? "Full SQLite quick_check returned OK."
                : "SQLite quick_check returned: " + result;
        }
        catch (Exception error)
        {
            MarkStorageError(error);
            Volatile.Write(ref lastIntegrityResult, "Failed");
            health.Integrity = "Failed";
            health.Message = error.GetType().Name + ": " + error.Message;
        }
        finally
        {
            Interlocked.Exchange(ref lastIntegrityCheckMilliseconds, watch.ElapsedMilliseconds);
            Interlocked.Exchange(ref lastIntegrityCheckUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref integrityCheckRunning, 0);
        }
        health.LastIntegrityCheckMilliseconds = Interlocked.Read(ref lastIntegrityCheckMilliseconds);
        health.LastIntegrityCheckUnixMilliseconds = Interlocked.Read(ref lastIntegrityCheckUnixMilliseconds);
        health.IntegrityCheckInProgress = false;
        return health;
    }

    public void UpdateRuntimeSettings(
        int newMaxQueue,
        int newEmergencyOverflowCapacity,
        int newFlushIntervalMs,
        int newAutoBackupIntervalHours,
        int newBackupRetentionCount)
    {
        maxQueue = Math.Max(100, newMaxQueue);
        emergencyOverflowCapacity = Math.Max(1000, newEmergencyOverflowCapacity);
        flushIntervalMs = Math.Clamp(newFlushIntervalMs, 100, 5000);
        autoBackupIntervalHours = Math.Clamp(newAutoBackupIntervalHours, 0, 24 * 365);
        backupRetentionCount = Math.Clamp(newBackupRetentionCount, 1, 20);
        TrimDatabaseBackups();
    }

    public bool TryEnqueue(LogEventRecord record)
    {
        // TryEnqueueFast so dictionaries are never allocated on the game thread.
        string?[] kv = new string?[16];
        var i = 0;
        foreach (var pair in record.Meta)
        {
            if (i >= 16) break;
            kv[i++] = pair.Key;
            kv[i++] = pair.Value;
        }
        return TryEnqueueFast(new QueuedEventPayload(
            record.TimestampUtc, record.IngameDate, record.Action,
            record.PlayerUid, record.PlayerName, record.WorldId, record.Dimension,
            record.X, record.Y, record.Z, record.BlockCode,
            kv[0],kv[1],kv[2],kv[3],kv[4],kv[5],kv[6],kv[7],
            kv[8],kv[9],kv[10],kv[11],kv[12],kv[13],kv[14],kv[15]));
    }

    public bool TryEnqueueFast(in QueuedEventPayload record)
    {
        // Generic block/container/admin audit rows use the critical lane first. If the
        // normal queue is saturated, route them into the emergency reserve rather than
        // dropping them immediately.
        Interlocked.Increment(ref queuedCriticalEvents);

        var total = Interlocked.Increment(ref queuedEvents);
        ObservePeak(ref peakPendingEvents, total + Interlocked.Read(ref queuedOverflowEvents));
        if (total > maxQueue)
        {
            Interlocked.Decrement(ref queuedEvents);
            Interlocked.Decrement(ref queuedCriticalEvents);
            return TryEnqueueOverflow(record);
        }

        criticalEventQueue.Enqueue(record);
        if (total == 1 && Interlocked.Read(ref queuedOverflowEvents) == 0) eventSignal.Release();
        return true;
    }

    public bool TryEnqueueEntityFast(in QueuedEntityEventPayload record)
    {
        // Death/interact records use the critical lane. Spawn/despawn records keep their
        // own lane for writer fairness. Full-fidelity mode may use the entire normal queue;
        // the emergency reserve protects critical arrivals during short saturation bursts.
        var bulk = record.IsBulkEntity;
        var bulkCap = maxQueue;

        if (bulk)
        {
            var laneCount = Interlocked.Increment(ref queuedBulkEntityEvents);
            if (laneCount > bulkCap)
            {
                Interlocked.Decrement(ref queuedBulkEntityEvents);
                return TryEnqueueOverflow(record);
            }
        }
        else
        {
            Interlocked.Increment(ref queuedCriticalEvents);
        }

        var total = Interlocked.Increment(ref queuedEvents);
        ObservePeak(ref peakPendingEvents, total + Interlocked.Read(ref queuedOverflowEvents));
        if (total > maxQueue)
        {
            Interlocked.Decrement(ref queuedEvents);
            if (bulk) Interlocked.Decrement(ref queuedBulkEntityEvents);
            else Interlocked.Decrement(ref queuedCriticalEvents);
            return TryEnqueueOverflow(record);
        }

        if (bulk) bulkEntityQueue.Enqueue(record);
        else criticalEntityQueue.Enqueue(record);

        if (total == 1 && Interlocked.Read(ref queuedOverflowEvents) == 0) eventSignal.Release();
        return true;
    }

    private bool TryReserveOverflowSlot(out long pending)
    {
        pending = Interlocked.Increment(ref queuedOverflowEvents);
        if (pending > emergencyOverflowCapacity)
        {
            Interlocked.Decrement(ref queuedOverflowEvents);
            Interlocked.Increment(ref overflowRejectedEvents);
            pending = 0;
            return false;
        }

        Interlocked.Increment(ref overflowAcceptedEvents);
        ObservePeak(ref peakQueuedOverflowEvents, pending);
        ObservePeak(ref peakPendingEvents, Interlocked.Read(ref queuedEvents) + pending);
        return true;
    }

    private bool TryEnqueueOverflow(in QueuedEventPayload record)
    {
        if (!TryReserveOverflowSlot(out var pending))
        {
            Interlocked.Increment(ref droppedEvents);
            MaybeReportDrops();
            return false;
        }

        overflowEventQueue.Enqueue(record);
        // Match the normal lanes: a single wake-up is enough when the reserve
        // transitions from empty to non-empty. Avoid one semaphore signal per row.
        if (pending == 1) eventSignal.Release();
        return true;
    }

    private bool TryEnqueueOverflow(in QueuedEntityEventPayload record)
    {
        if (!TryReserveOverflowSlot(out var pending))
        {
            Interlocked.Increment(ref droppedEvents);
            Interlocked.Increment(ref droppedEntityEvents);
            MaybeReportDrops();
            return false;
        }

        overflowEntityQueue.Enqueue(record);
        if (pending == 1) eventSignal.Release();
        return true;
    }


    public bool TryEnqueueEntityAggregate(EntityActivityAggregate aggregate)
    {
        var aggregateCap = Math.Max(128, maxQueue / 4);
        var pending = Interlocked.Increment(ref queuedEntityAggregates);
        if (pending > aggregateCap)
        {
            Interlocked.Decrement(ref queuedEntityAggregates);
            Interlocked.Increment(ref droppedEntityAggregates);
            MaybeReportDrops();
            return false;
        }

        ObservePeak(ref peakQueuedEntityAggregates, pending);
        entityAggregateQueue.Enqueue(aggregate);
        if (pending == 1) eventSignal.Release();
        return true;
    }

    public bool TryEnqueue(ContainerSnapshotRecord record)
    {
        var count = Interlocked.Increment(ref queuedSnapshots);
        if (count > maxQueue)
        {
            Interlocked.Decrement(ref queuedSnapshots);
            Interlocked.Increment(ref droppedSnapshots);
            MaybeReportDrops();
            return false;
        }

        snapshotQueue.Enqueue(record);
        if (count == 1) eventSignal.Release();
        return true;
    }

    private void MaybeReportDrops()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastDropWarningTicks);
        if (now - last < TimeSpan.FromSeconds(30).Ticks) return;
        if (Interlocked.CompareExchange(ref lastDropWarningTicks, now, last) != last) return;

        api.Logger.Warning(
            "[BlockLogger] Storage queue overloaded. Pending={0} (normalCap={1}, overflow={2}/{3}, critical={4}, bulkEntity={5}, aggregates={6}), OverflowAccepted={7}, OverflowRejected={8}, DroppedEvents={9}, DroppedAggregates={10}, DroppedSnapshots={11}. Further warnings are rate-limited.",
            PendingEvents, maxQueue, PendingOverflowEvents, emergencyOverflowCapacity, PendingCriticalEvents, PendingBulkEntityEvents, PendingEntityAggregates,
            OverflowAcceptedEvents, OverflowRejectedEvents, DroppedEvents, DroppedEntityAggregates, DroppedSnapshots);
    }

    public Task<List<LogEventRecord>> QueryEventsAsync(
        int x, int y, int z, int dimension, int radius, int maxCount, string? text, string? tag, bool entityOnly, int offset = 0)
    {
        var tcs = NewTcs<List<LogEventRecord>>();
        if (!TryAddQuery(queryQueue, new EventRequest(
                x, y, z, dimension, radius, maxCount, Math.Clamp(offset, 0, 2000), text, tag, entityOnly, false, null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tcs)))
            tcs.TrySetException(new InvalidOperationException("BlockLogger query queue is busy or stopped."));
        return tcs.Task;
    }

    public Task<List<LogEventRecord>> QueryPlayerEventsAsync(
        string player, int maxCount, string? category, int offset = 0)
    {
        var tcs = NewTcs<List<LogEventRecord>>();
        if (!TryAddQuery(queryQueue, new EventRequest(
                0, 0, 0, 0, 0, maxCount, Math.Clamp(offset, 0, 2000), null, category, false, true, player,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tcs)))
            tcs.TrySetException(new InvalidOperationException("BlockLogger query queue is busy or stopped."));
        return tcs.Task;
    }

    public Task<List<LogEventRecord>> QueryPlayerUidEventsAsync(string uid, int maxCount = 100)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 128) throw new ArgumentException("A player UID is required.", nameof(uid));
        var tcs = NewTcs<List<LogEventRecord>>();
        if (!TryAddQuery(queryQueue, new EventRequest(0, 0, 0, 0, 0, Math.Clamp(maxCount, 1, 100), 0, null, null,
                false, true, uid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tcs) { ExactUid = true }))
            tcs.TrySetException(new InvalidOperationException("BlockLogger query queue is busy or stopped."));
        return tcs.Task;
    }

    public Task<RollbackHistory> QueryRollbackRecoveryAsync(string target, DateTime fromUtc, DateTime toUtc, RecoveryCell? areaCenter = null, int areaRadius = 0, CancellationToken cancellationToken = default) =>
        recoveryQueries.RunAsync(OpenReadOnlyConnection, (conn, token) =>
        {
            bool area=areaCenter!=null;
            // Radius zero deliberately selects exactly one block, including its full conflict history.
            if(area && (areaRadius<0 || areaRadius>4096))throw new ArgumentOutOfRangeException(nameof(areaRadius));
            if(fromUtc>=toUtc || toUtc>DateTime.UtcNow || toUtc-fromUtc>TimeSpan.FromDays(7))throw new ArgumentException("Invalid recovery time window.");
            if (!IsReady || PendingCriticalEvents != 0 || PendingOverflowEvents != 0 || PendingSnapshots != 0)
                throw new InvalidOperationException("Logger is indexing or writing recovery records; retry when critical/overflow/snapshot queues are empty.");
            token.ThrowIfCancellationRequested();
            {
                using var transaction = conn.BeginTransaction(deferred: true);
                // Queue counters drop at dequeue, not commit. Wait for the writer barrier and
                // pin a read snapshot only when no accepted recovery rows remain in flight.
                if(!writeGate.Wait(5000,token))throw new InvalidOperationException("Logger writer is busy; retry recovery preview.");
                try
                {
                    if(PendingCriticalEvents!=0 || PendingOverflowEvents!=0 || PendingSnapshots!=0 || Volatile.Read(ref recoveryWriterInFlight)!=0)
                        throw new InvalidOperationException("Recovery records are still pending/in-flight; retry after the logger commits them.");
                    using var pin=conn.CreateCommand();pin.Transaction=transaction;pin.CommandText="SELECT id FROM events LIMIT 1;";pin.ExecuteScalar();
                }
                finally {writeGate.Release();}
                string uid="";
                if(!area)
                {
                using var resolve = conn.CreateCommand();
                resolve.Transaction = transaction;
                resolve.CommandText = @"SELECT DISTINCT player_uid FROM events INDEXED BY idx_events_world_time
                    WHERE world_id=$world AND timestamp_ticks BETWEEN $from AND $to
                    AND player_uid<>'' AND player_uid=$target COLLATE NOCASE
                    UNION SELECT DISTINCT player_uid FROM events INDEXED BY idx_events_player_name_time
                    WHERE world_id=$world AND timestamp_ticks BETWEEN $from AND $to
                    AND player_uid<>'' AND player_name=$target COLLATE NOCASE
                    UNION SELECT DISTINCT json_extract(CASE WHEN json_valid(meta_json) THEN meta_json ELSE '{}' END,'$.sourcePlayerUid')
                    FROM events INDEXED BY idx_events_world_time WHERE world_id=$world AND action=9 AND timestamp_ticks BETWEEN $from AND $to
                    AND (json_extract(CASE WHEN json_valid(meta_json) THEN meta_json ELSE '{}' END,'$.sourcePlayerUid')=$target COLLATE NOCASE
                         OR json_extract(CASE WHEN json_valid(meta_json) THEN meta_json ELSE '{}' END,'$.sourcePlayerName')=$target COLLATE NOCASE);";
                resolve.Parameters.AddWithValue("$world",currentWorldId);
                resolve.Parameters.AddWithValue("$from",fromUtc.Ticks);
                resolve.Parameters.AddWithValue("$to",toUtc.Ticks);
                resolve.Parameters.AddWithValue("$target",target);
                var uids = new HashSet<string>(StringComparer.Ordinal);
                using(var reader=resolve.ExecuteReader()) while(reader.Read()) { token.ThrowIfCancellationRequested(); if(!reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0))) uids.Add(reader.GetString(0)); }
                if(uids.Count!=1) throw new InvalidOperationException("Exact name/UID not found or ambiguous in the selected time window. Use the UID.");
                uid=uids.First();
                }
                var result=new RollbackHistory { AreaMode=area,PlayerUid=uid,WorldId=currentWorldId };
                token.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"WITH cells AS MATERIALIZED (
                    SELECT DISTINCT CASE WHEN dimension='overworld' THEN '0' ELSE dimension END AS dimension,x,y,z FROM (
                        SELECT dimension,x,y,z FROM events INDEXED BY idx_events_player_uid_time
                        WHERE $area=0 AND world_id=$world AND player_uid=$uid AND player_uid<>''
                        AND timestamp_ticks BETWEEN $from AND $to AND action IN (1,2,3,4,5,6)
                        UNION ALL
                        SELECT dimension,x,y,z FROM events INDEXED BY idx_events_world_dim_pos_time
                        WHERE $area=1 AND world_id=$world
                        AND dimension IN ($dimension,CASE WHEN $dimension='0' THEN 'overworld' ELSE $dimension END)
                        AND x BETWEEN $minX AND $maxX AND y BETWEEN $minY AND $maxY AND z BETWEEN $minZ AND $maxZ
                        AND (1.0*(x-$cx)*(x-$cx)+1.0*(y-$cy)*(y-$cy)+1.0*(z-$cz)*(z-$cz))<=$r2
                        AND timestamp_ticks BETWEEN $from AND $to AND action IN (1,2,3,4,5,6)
                        UNION ALL SELECT $dimension,$cx,$cy,$cz WHERE $area=1 AND $r2=0
                    )
                ), baselines AS MATERIALIZED (
                    SELECT c.*, (SELECT MAX(prior.timestamp_ticks) FROM snapshots prior INDEXED BY idx_snapshots_world_dim_pos_time
                        WHERE prior.world_id=$world AND prior.dimension IN (c.dimension,CASE WHEN c.dimension='0' THEN 'overworld' ELSE c.dimension END)
                        AND prior.x=c.x AND prior.y=c.y AND prior.z=c.z AND prior.timestamp_ticks<$from) AS before_ticks FROM cells c
                )
                SELECT e.timestamp_ticks,e.ingame_date,e.action,e.player_uid,e.player_name,e.world_id,e.dimension,e.x,e.y,e.z,e.block_code,e.meta_json,e.id
                FROM baselines c CROSS JOIN events e INDEXED BY idx_events_world_dim_pos_time
                WHERE e.world_id=$world AND e.dimension IN (c.dimension,CASE WHEN c.dimension='0' THEN 'overworld' ELSE c.dimension END)
                AND e.x=c.x AND e.y=c.y AND e.z=c.z
                AND e.timestamp_ticks>=COALESCE(c.before_ticks+1,$from) AND e.action IN (1,2,3,4,5,6)
                UNION ALL
                SELECT e.timestamp_ticks,e.ingame_date,e.action,e.player_uid,e.player_name,e.world_id,e.dimension,e.x,e.y,e.z,e.block_code,e.meta_json,e.id FROM events e INDEXED BY idx_events_world_time
                WHERE $area=0 AND e.world_id=$world AND e.action=9 AND e.timestamp_ticks BETWEEN $from AND $to
                AND (e.player_uid=$uid OR json_extract(CASE WHEN json_valid(e.meta_json) THEN e.meta_json ELSE '{}' END,'$.sourcePlayerUid')=$uid)
                UNION ALL
                SELECT e.timestamp_ticks,e.ingame_date,e.action,e.player_uid,e.player_name,e.world_id,e.dimension,e.x,e.y,e.z,e.block_code,e.meta_json,e.id FROM events e INDEXED BY idx_events_world_dim_pos_time
                WHERE $area=1 AND e.world_id=$world AND e.action=9 AND e.timestamp_ticks BETWEEN $from AND $to
                AND e.dimension IN ($dimension,CASE WHEN $dimension='0' THEN 'overworld' ELSE $dimension END)
                AND e.x BETWEEN $minX AND $maxX AND e.y BETWEEN $minY AND $maxY AND e.z BETWEEN $minZ AND $maxZ
                AND (1.0*(e.x-$cx)*(e.x-$cx)+1.0*(e.y-$cy)*(e.y-$cy)+1.0*(e.z-$cz)*(e.z-$cz))<=$r2
                ORDER BY timestamp_ticks DESC,id DESC LIMIT 200001;";
                cmd.Parameters.AddWithValue("$world",currentWorldId);
                cmd.Parameters.AddWithValue("$uid",uid);
                AddAreaParameters(cmd,areaCenter,areaRadius);
                cmd.Parameters.AddWithValue("$from",fromUtc.Ticks);
                cmd.Parameters.AddWithValue("$to",toUtc.Ticks);
                using(var reader=cmd.ExecuteReader()) while(reader.Read()) { token.ThrowIfCancellationRequested(); var row=ReadEvent(reader); row.StorageId=reader.GetInt64(12); result.Events.Add(row); }
                if(result.Events.Count>200000) throw new InvalidOperationException("Affected-location history exceeds 200,000 rows. Choose a shorter time window.");
                token.ThrowIfCancellationRequested();
                using var snapshots = conn.CreateCommand();
                snapshots.Transaction=transaction;
                snapshots.CommandText = @"WITH cells AS MATERIALIZED (
                    SELECT DISTINCT CASE WHEN dimension='overworld' THEN '0' ELSE dimension END AS dimension,x,y,z FROM (
                        SELECT dimension,x,y,z FROM events INDEXED BY idx_events_player_uid_time
                        WHERE $area=0 AND world_id=$world AND player_uid=$uid AND player_uid<>''
                        AND timestamp_ticks BETWEEN $from AND $to AND action IN (1,2,3,4,5,6)
                        UNION ALL
                        SELECT dimension,x,y,z FROM events INDEXED BY idx_events_world_dim_pos_time
                        WHERE $area=1 AND world_id=$world
                        AND dimension IN ($dimension,CASE WHEN $dimension='0' THEN 'overworld' ELSE $dimension END)
                        AND x BETWEEN $minX AND $maxX AND y BETWEEN $minY AND $maxY AND z BETWEEN $minZ AND $maxZ
                        AND (1.0*(x-$cx)*(x-$cx)+1.0*(y-$cy)*(y-$cy)+1.0*(z-$cz)*(z-$cz))<=$r2
                        AND timestamp_ticks BETWEEN $from AND $to AND action IN (1,2,3,4,5,6)
                        UNION ALL SELECT $dimension,$cx,$cy,$cz WHERE $area=1 AND $r2=0
                    )
                ), baselines AS MATERIALIZED (
                    SELECT c.*, (SELECT old.id FROM snapshots old INDEXED BY idx_snapshots_world_dim_pos_time
                        WHERE old.world_id=$world AND old.dimension IN (c.dimension,CASE WHEN c.dimension='0' THEN 'overworld' ELSE c.dimension END)
                        AND old.x=c.x AND old.y=c.y AND old.z=c.z AND old.timestamp_ticks<$from
                        ORDER BY old.timestamp_ticks DESC,old.id DESC LIMIT 1) AS before_id FROM cells c
                )
                SELECT s.timestamp_ticks,s.ingame_date,s.world_id,s.dimension,s.x,s.y,s.z,s.block_code,s.player_uid,s.player_name,s.inventory_id,s.reason,s.changed_slot_id,s.slot_count,s.non_empty_slot_count,s.total_item_count,s.slots_json,s.id
                FROM baselines c CROSS JOIN snapshots s INDEXED BY idx_snapshots_world_dim_pos_time
                WHERE s.world_id=$world AND s.dimension IN (c.dimension,CASE WHEN c.dimension='0' THEN 'overworld' ELSE c.dimension END)
                AND s.x=c.x AND s.y=c.y AND s.z=c.z AND s.timestamp_ticks >= $from
                UNION ALL
                SELECT s.timestamp_ticks,s.ingame_date,s.world_id,s.dimension,s.x,s.y,s.z,s.block_code,s.player_uid,s.player_name,s.inventory_id,s.reason,s.changed_slot_id,s.slot_count,s.non_empty_slot_count,s.total_item_count,s.slots_json,s.id FROM baselines c CROSS JOIN snapshots s
                WHERE s.id=c.before_id AND s.world_id=$world
                ORDER BY timestamp_ticks DESC,id DESC LIMIT 50001;";
                snapshots.Parameters.AddWithValue("$world",currentWorldId);
                snapshots.Parameters.AddWithValue("$uid",uid);
                AddAreaParameters(snapshots,areaCenter,areaRadius);
                snapshots.Parameters.AddWithValue("$from",fromUtc.Ticks);
                snapshots.Parameters.AddWithValue("$to",toUtc.Ticks);
                using(var reader=snapshots.ExecuteReader()) while(reader.Read()) {
                    token.ThrowIfCancellationRequested();
                    if(reader.IsDBNull(16) || Newtonsoft.Json.Linq.JToken.Parse(reader.GetString(16)).Type != Newtonsoft.Json.Linq.JTokenType.Array)
                        throw new InvalidDataException("Historical inventory snapshot JSON is invalid; no recovery was planned.");
                    var row=ReadSnapshot(reader); row.StorageId=reader.GetInt64(17); result.Snapshots.Add(row); }
                if(result.Snapshots.Count>50000) throw new InvalidOperationException("Recovery snapshot history exceeds 50,000 rows; shorten the time window.");
                transaction.Commit();
                return result;
            }
        }, cts.Token, cancellationToken);

    private static void AddAreaParameters(SqliteCommand command,RecoveryCell? center,int radius)
    {
        command.Parameters.AddWithValue("$area",center==null?0:1);
        command.Parameters.AddWithValue("$dimension",center==null?"":RecoveryCell.Normalize(center.Dimension));
        long x=center?.X??0,y=center?.Y??0,z=center?.Z??0;
        command.Parameters.AddWithValue("$cx",x);command.Parameters.AddWithValue("$cy",y);command.Parameters.AddWithValue("$cz",z);
        command.Parameters.AddWithValue("$minX",x-radius);command.Parameters.AddWithValue("$maxX",x+radius);
        command.Parameters.AddWithValue("$minY",y-radius);command.Parameters.AddWithValue("$maxY",y+radius);
        command.Parameters.AddWithValue("$minZ",z-radius);command.Parameters.AddWithValue("$maxZ",z+radius);
        command.Parameters.AddWithValue("$r2",(long)radius*radius);
    }

    public Task<List<ContainerSnapshotRecord>> QuerySnapshotsAsync(
        int x, int y, int z, int dimension, int radius, int maxCount, string? text)
    {
        var tcs = NewTcs<List<ContainerSnapshotRecord>>();
        if (!TryAddQuery(snapshotQueryQueue, new SnapshotRequest(x, y, z, dimension, radius, maxCount, text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), tcs)))
            tcs.TrySetException(new InvalidOperationException("BlockLogger snapshot query queue is busy or stopped."));
        return tcs.Task;
    }

    public Task<List<BlockLogger.WebDashboard.PerformanceBlockActivityHotspot>>
        QueryRecentBlockActivityHotspotsAsync(int minutes, int maxCount)
    {
        minutes = Math.Clamp(minutes, 1, 1440);
        maxCount = Math.Clamp(maxCount, 1, 200);
        long cutoff = DateTime.UtcNow.AddMinutes(-minutes).Ticks;

        return Task.Run(() => WithReadOnlyConnection(conn =>
        {
            var rows =
                new List<BlockLogger.WebDashboard.PerformanceBlockActivityHotspot>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    dimension,
    x,
    y,
    z,
    COALESCE(block_code, ''),
    COUNT(*) AS event_count,
    COUNT(DISTINCT action) AS action_count,
    MAX(timestamp_ticks) AS last_ticks
FROM events
WHERE world_id = $worldId
  AND timestamp_ticks >= $cutoff
  AND COALESCE(block_code, '') <> ''
GROUP BY dimension, x, y, z, block_code
ORDER BY event_count DESC, last_ticks DESC
LIMIT $limit;";

            cmd.Parameters.AddWithValue("$worldId", currentWorldId);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$limit", maxCount);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(
                    new BlockLogger.WebDashboard.PerformanceBlockActivityHotspot
                    {
                        Dimension = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        X = reader.GetInt32(1),
                        Y = reader.GetInt32(2),
                        Z = reader.GetInt32(3),
                        BlockCode = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        EventCount = reader.GetInt32(5),
                        DistinctActions = reader.GetInt32(6),
                        LastTimestampTicks = reader.GetInt64(7)
                    });
            }

            return rows;
        }));
    }

    public Task<int> CountEventsAsync()
    {
        return Task.Run(() => WithReadOnlyConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }));
    }

    public Task<int> CountSnapshotsAsync()
    {
        return Task.Run(() => WithReadOnlyConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }));
    }

    public Task<int> PruneAsync(int retentionDays)
    {
        if (retentionDays <= 0) return Task.FromResult(0);
        if (Interlocked.CompareExchange(ref retentionInProgress, 1, 0) != 0)
            return Task.FromResult(0);

        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            int removedTotal = 0;
            try
            {
                long cutoff = DateTime.UtcNow.AddDays(-retentionDays).Ticks;

                // Run bounded small transactions, releasing the writer gate between batches.
                // The old four-pass ceiling could fall behind on busy long-lived servers;
                // 24 quiet passes still remain bounded while allowing retention to catch up.
                // Live audit traffic always gets a chance to cut maintenance short.
                for (int pass = 0; pass < 8 && !cts.IsCancellationRequested; pass++)
                {
                    if (pass > 0 && (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0))
                        break;

                    writeGate.Wait(cts.Token);
                    int removedThisPass;
                    try
                    {
                        removedThisPass = WithConnection(conn =>
                        {
                            using var tx = conn.BeginTransaction();
                            int removed = 0;

                            using (var e = conn.CreateCommand())
                            {
                                e.Transaction = tx;
                                e.CommandText = @"DELETE FROM events WHERE id IN (
                                    SELECT id FROM events WHERE timestamp_ticks < $cutoff ORDER BY id LIMIT 500
                                );";
                                e.Parameters.AddWithValue("$cutoff", cutoff);
                                removed += e.ExecuteNonQuery();
                            }

                            using (var ss = conn.CreateCommand())
                            {
                                ss.Transaction = tx;
                                ss.CommandText = @"DELETE FROM snapshots WHERE id IN (
                                    SELECT id FROM snapshots WHERE timestamp_ticks < $cutoff ORDER BY id LIMIT 100
                                );";
                                ss.Parameters.AddWithValue("$cutoff", cutoff);
                                removed += ss.ExecuteNonQuery();
                            }

                            using (var ea = conn.CreateCommand())
                            {
                                ea.Transaction = tx;
                                ea.CommandText = @"DELETE FROM entity_activity WHERE id IN (
                                    SELECT id FROM entity_activity WHERE window_end_ticks < $cutoff ORDER BY id LIMIT 500
                                );";
                                ea.Parameters.AddWithValue("$cutoff", cutoff);
                                removed += ea.ExecuteNonQuery();
                            }

                            tx.Commit();
                            return removed;
                        });
                    }
                    finally { writeGate.Release(); }

                    removedTotal += removedThisPass;
                    if (removedThisPass == 0) break;

                    // Keep retention deliberately low priority. Releasing the writer gate is not
                    // enough by itself on SQLite/WAL: immediately reacquiring it can still starve
                    // live audit batches. A tiny yield gives queued gameplay writes first chance.
                    if (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)
                        break;
                    Thread.Sleep(5);
                }

                return removedTotal;
            }
            catch (OperationCanceledException) { return removedTotal; }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                api.Logger.Warning("[BlockLogger/Storage] Retention maintenance failed: {0}: {1}", ex.GetType().Name, ex.Message);
                return removedTotal;
            }
            finally
            {
                sw.Stop();
                SuiteTimingRegistry.Record("BlockLogger retention", sw.Elapsed.TotalMilliseconds);
                Interlocked.Exchange(ref retentionInProgress, 0);
            }
        });
    }

    public Task<BlockLoggerPurgeResult> PurgeDataAsync(
        int keepDays,
        bool purgeEvents,
        bool purgeSnapshots,
        bool purgeEntityActivity,
        bool compactAfter)
    {
        keepDays = Math.Clamp(keepDays, 0, 3650);
        if (!purgeEvents && !purgeSnapshots && !purgeEntityActivity)
            return Task.FromResult(new BlockLoggerPurgeResult { Success = false, Message = "Select at least one BlockLogger data category to purge." });
        if (Interlocked.CompareExchange(ref retentionInProgress, 1, 0) != 0)
            return Task.FromResult(new BlockLoggerPurgeResult { Success = false, Message = "BlockLogger retention/purge maintenance is already running." });

        return Task.Run(() =>
        {
            var result = new BlockLoggerPurgeResult
            {
                KeepDays = keepDays,
                DatabaseBytesBefore = SafeFileLength(dbPath),
                WalBytesBefore = SafeFileLength(dbPath + "-wal")
            };
            var started = Stopwatch.StartNew();
            long cutoffTicks = keepDays <= 0 ? long.MaxValue : DateTime.UtcNow.AddDays(-keepDays).Ticks;
            const int maxRowsPerRun = 500_000;
            const int maxSecondsPerRun = 45;
            int totalRemoved = 0;

            try
            {
                while (!cts.IsCancellationRequested && totalRemoved < maxRowsPerRun && started.Elapsed.TotalSeconds < maxSecondsPerRun)
                {
                    int removedThisPass = 0;
                    writeGate.Wait(cts.Token);
                    try
                    {
                        removedThisPass = WithConnection(conn =>
                        {
                            using var tx = conn.BeginTransaction();
                            int removed = 0;
                            if (purgeEvents)
                            {
                                int n = DeletePurgeBatch(conn, tx, "events", "timestamp_ticks", cutoffTicks, keepDays <= 0, 5000);
                                result.EventsRemoved += n; removed += n;
                            }
                            if (purgeSnapshots)
                            {
                                int n = DeletePurgeBatch(conn, tx, "snapshots", "timestamp_ticks", cutoffTicks, keepDays <= 0, 1000);
                                result.SnapshotsRemoved += n; removed += n;
                            }
                            if (purgeEntityActivity)
                            {
                                int n = DeletePurgeBatch(conn, tx, "entity_activity", "window_end_ticks", cutoffTicks, keepDays <= 0, 5000);
                                result.EntityActivityRemoved += n; removed += n;
                            }
                            tx.Commit();
                            return removed;
                        });
                    }
                    finally { writeGate.Release(); }

                    totalRemoved += removedThisPass;
                    if (removedThisPass == 0)
                    {
                        result.Complete = true;
                        break;
                    }

                    // Give live audit traffic a chance to use the writer between maintenance batches.
                    if (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)
                        Thread.Sleep(25);
                }

                if (!result.Complete)
                    result.MoreRowsMayRemain = true;

                // A truncate checkpoint can reclaim a large WAL immediately without rewriting
                // the whole database. It is safe to skip if live traffic is currently queued.
                if (PendingEvents == 0 && PendingSnapshots == 0 && PendingEntityAggregates == 0)
                {
                    writeGate.Wait(cts.Token);
                    try
                    {
                        WithConnection(conn =>
                        {
                            using var checkpoint = conn.CreateCommand();
                            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            checkpoint.ExecuteNonQuery();
                            return 0;
                        });
                    }
                    catch { }
                    finally { writeGate.Release(); }
                }

                if (compactAfter && result.Complete)
                {
                    result.CompactRequested = true;
                    TryCompactDatabase(result);
                }

                result.DatabaseBytesAfter = SafeFileLength(dbPath);
                result.WalBytesAfter = SafeFileLength(dbPath + "-wal");
                result.DurationMilliseconds = started.ElapsedMilliseconds;
                result.Success = true;
                result.Message = result.MoreRowsMayRemain
                    ? $"Removed {totalRemoved:N0} rows in this bounded maintenance pass. More matching rows may remain; run Purge again to continue."
                    : $"Purge completed. Removed {totalRemoved:N0} rows." + (result.Compacted ? " Database compaction completed." : "");
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Message = "Purge cancelled because BlockLogger storage is shutting down.";
                return result;
            }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                result.Message = $"{ex.GetType().Name}: {ex.Message}";
                return result;
            }
            finally
            {
                started.Stop();
                SuiteTimingRegistry.Record("BlockLogger manual purge", started.Elapsed.TotalMilliseconds);
                Interlocked.Exchange(ref retentionInProgress, 0);
            }
        });
    }

    private static int DeletePurgeBatch(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string timeColumn,
        long cutoffTicks,
        bool purgeAll,
        int limit)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = purgeAll
            ? $"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} ORDER BY id LIMIT $limit);"
            : $"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} WHERE {timeColumn} < $cutoff ORDER BY id LIMIT $limit);";
        cmd.Parameters.AddWithValue("$limit", limit);
        if (!purgeAll) cmd.Parameters.AddWithValue("$cutoff", cutoffTicks);
        return Math.Max(0, cmd.ExecuteNonQuery());
    }

    private void TryCompactDatabase(BlockLoggerPurgeResult result)
    {
        try
        {
            if (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)
            {
                result.CompactMessage = "Compaction skipped because live BlockLogger writes are queued. Run it again during a quiet maintenance window.";
                return;
            }

            long dbBytes = SafeFileLength(dbPath);
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(dbPath)) ?? "";
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    long required = Math.Max(512L * 1024 * 1024, (long)(dbBytes * 1.15));
                    if (drive.AvailableFreeSpace < required)
                    {
                        result.CompactMessage = $"Compaction skipped: about {required / 1048576d:0} MiB free space is required, but only {drive.AvailableFreeSpace / 1048576d:0} MiB is available.";
                        return;
                    }
                }
            }
            catch { }

            writeGate.Wait(cts.Token);
            try
            {
                using var conn = OpenConnection();
                using (var vacuum = conn.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                }
                using (var checkpoint = conn.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    checkpoint.ExecuteNonQuery();
                }
                result.Compacted = true;
                result.CompactMessage = "VACUUM completed and the WAL was truncated.";
            }
            finally { writeGate.Release(); }
        }
        catch (Exception ex)
        {
            result.CompactMessage = $"Compaction failed/skipped: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static long SafeFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private void ProbeStartupDatabase(bool fullCheck)
    {
        if(!File.Exists(dbPath))return;
        var result=LoggerDatabaseStartup.Probe(dbPath,fullCheck);
        Volatile.Write(ref lastIntegrityResult,result);
    }

    private void MeasureStartup(string stage,Action work)
    {
        api.Logger.Notification("[BlockLogger/Startup] {0} starting.",stage);
        var watch=Stopwatch.StartNew();bool complete=false;
        try{work();complete=true;}
        finally
        {
            startupMilliseconds[stage]=watch.ElapsedMilliseconds;
            SuiteTimingRegistry.Record("BlockLogger startup: "+stage,watch.Elapsed.TotalMilliseconds);
            api.Logger.Notification("[BlockLogger/Startup] {0} {1} in {2}ms.",stage,complete?"completed":"failed",watch.ElapsedMilliseconds);
        }
    }
    public IReadOnlyDictionary<string, long> StartupMilliseconds { get; }
    public string StartupValidationMode {get;}
    private readonly Dictionary<string,long> startupMilliseconds=new(StringComparer.Ordinal);
    private readonly long storageStarted;
    private readonly RecoveryQueryRunner integrityQueries=new();
    private int integrityCheckRunning;

    private void MigrateLegacyDatabaseIfNeeded(string legacyDbPath, string newDbPath)
    {
        if (File.Exists(newDbPath) || !File.Exists(legacyDbPath)) return;
        try
        {
            api.Logger.Notification("[BlockLogger/Storage] Migrating SQLite database into Vintage Story data folder: {0}", newDbPath);
            using var source = new SqliteConnection($"Data Source={legacyDbPath};Mode=ReadOnly;Pooling=False");
            source.Open();
            using var target = new SqliteConnection($"Data Source={newDbPath};Mode=ReadWriteCreate;Pooling=False");
            target.Open();
            source.BackupDatabase(target);
            int removedOtherWorldRows = 0;
            using (var tx = target.BeginTransaction())
            {
                foreach (var table in new[] { "events", "snapshots", "entity_activity" })
                {
                    try
                    {
                        using var normalize = target.CreateCommand();
                        normalize.Transaction = tx;
                        normalize.CommandText = $"UPDATE {table} SET world_id=$world WHERE world_id IS NULL OR world_id='';";
                        normalize.Parameters.AddWithValue("$world", currentWorldId);
                        normalize.ExecuteNonQuery();

                        using var filter = target.CreateCommand();
                        filter.Transaction = tx;
                        filter.CommandText = $"DELETE FROM {table} WHERE world_id<>$world;";
                        filter.Parameters.AddWithValue("$world", currentWorldId);
                        removedOtherWorldRows += Math.Max(0, filter.ExecuteNonQuery());
                    }
                    catch (SqliteException)
                    {
                        // Very old legacy schemas may not have all current tables/columns.
                    }
                }
                tx.Commit();
            }
            api.Logger.Notification("[BlockLogger/Storage] Legacy blocklogger.db migration completed for world {0}. Removed {1:N0} row(s) belonging to other worlds from the new per-world copy. Original database was left untouched as a rollback copy.", currentWorldId, removedOtherWorldRows);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[BlockLogger/Storage] Legacy database migration failed; the new database will not replace the original: {0}", ex);
            try { if (File.Exists(newDbPath)) File.Delete(newDbPath); } catch { }
        }
    }

    public Task<string> CreateDatabaseBackupAsync(string reason = "manual")
    {
        return Task.Run(() => CreateDatabaseBackup(reason));
    }

    private string CreateDatabaseBackup(string reason)
    {
        if (cts.IsCancellationRequested) return "";
        if (Interlocked.CompareExchange(ref backupInProgress, 1, 0) != 0) return "";
        try
        {
            string cleanReason = new string((reason ?? "backup").Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(24).ToArray());
            if (string.IsNullOrWhiteSpace(cleanReason)) cleanReason = "backup";
            var targetPath = Path.Combine(backupsPath, $"blocklogger-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{cleanReason}.db");
            bool maintenanceGateHeld = false;
            try
            {
                // Checkpoint/optimize only when the writer is immediately available. Keep
                // every writer-oriented PRAGMA behind the same gate as live audit writes.
                maintenanceGateHeld = writeGate.Wait(0);
                if (maintenanceGateHeld && PendingEvents == 0 && PendingSnapshots == 0 && PendingEntityAggregates == 0)
                {
                    var maintenanceSw = Stopwatch.StartNew();
                    using var maintenanceSource = OpenConnection();
                    using var maintenance = maintenanceSource.CreateCommand();
                    maintenance.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
                    maintenance.ExecuteNonQuery();
                    maintenanceSw.Stop();
                    SuiteTimingRegistry.Record("BlockLogger SQLite maintenance", maintenanceSw.Elapsed.TotalMilliseconds);
                }
            }
            catch { }
            finally
            {
                if (maintenanceGateHeld) writeGate.Release();
            }

            // Keep the backup source read-only so opening it never issues writer PRAGMAs.
            // Microsoft.Data.Sqlite performs BackupDatabase as a fast full copy, so automatic
            // backups are scheduled only after a quiet period below; a manual backup may still
            // briefly contend with live writes on a very large database.
            var backupSw = Stopwatch.StartNew();
            using var source = OpenReadOnlyConnection();
            using var target = new SqliteConnection($"Data Source={targetPath};Mode=ReadWriteCreate;Pooling=False");
            target.Open();
            source.BackupDatabase(target);
            backupSw.Stop();
            SuiteTimingRegistry.Record("BlockLogger database backup", backupSw.Elapsed.TotalMilliseconds);
            Interlocked.Exchange(ref lastBackupUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            TrimDatabaseBackups();
            return targetPath;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger/Storage] Database backup failed: {0}", ex.Message);
            return "";
        }
        finally
        {
            Interlocked.Exchange(ref backupInProgress, 0);
        }
    }

    private long GetBackupBytes(bool includeCorrupt)
    {
        if (!Directory.Exists(backupsPath)) return 0;
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(backupsPath, "blocklogger-*.db"))
            {
                bool corrupt = Path.GetFileName(file).Contains("corrupt", StringComparison.OrdinalIgnoreCase);
                if (!includeCorrupt && corrupt) continue;
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private void TrimDatabaseBackups()
    {
        if (!Directory.Exists(backupsPath)) return;
        try
        {
            var healthy = Directory.EnumerateFiles(backupsPath, "blocklogger-*.db")
                .Where(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (string file in healthy.Skip(Math.Clamp(backupRetentionCount, 1, 20)))
            {
                try { File.Delete(file); } catch { }
            }

            // Quarantined originals are evidence, not ordinary rotation backups.
            // Retain them for explicit administrator review/removal.
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger/Storage] Could not trim database backups: {0}", ex.Message);
        }
    }

    private void MaybeScheduleDatabaseBackup(bool force)
    {
        try
        {
            if(!force&&LoggerDatabaseStartup.DeferAutomaticBackup(api.Server.CurrentRunPhase==Vintagestory.API.Server.EnumServerRunPhase.RunGame,Stopwatch.GetElapsedTime(storageStarted)))return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!force && now - Interlocked.Read(ref lastAutoBackupCheckUnixMilliseconds) < 60L * 60 * 1000) return;
            Interlocked.Exchange(ref lastAutoBackupCheckUnixMilliseconds, now);
            CheckDatabaseGrowth(now);
            if (!force && autoBackupIntervalHours <= 0) return;
            if (Volatile.Read(ref backupInProgress) != 0 || cts.IsCancellationRequested) return;
            if (!force && (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)) return;
            // BackupDatabase is not incremental in Microsoft.Data.Sqlite. Avoid starting an
            // automatic full copy during active audit traffic; startup/warmup is excluded
            // separately before updating the eligibility check timestamp.
            long lastWrite = Interlocked.Read(ref lastSuccessfulWriteUnixMilliseconds);
            if (!force && lastWrite > 0 && now - lastWrite < 10L * 60 * 1000) return;

            var newest = Directory.EnumerateFiles(backupsPath, "blocklogger-*.db")
                .Where(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (!force && newest != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) < TimeSpan.FromHours(Math.Max(1, autoBackupIntervalHours)))
            {
                Interlocked.Exchange(ref lastBackupUnixMilliseconds, new DateTimeOffset(File.GetLastWriteTimeUtc(newest), TimeSpan.Zero).ToUnixTimeMilliseconds());
                return;
            }

            _ = Task.Factory.StartNew(() =>
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                CreateDatabaseBackup(force ? "manual" : "auto");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger/Storage] Could not schedule database backup: {0}", ex.Message);
        }
    }


    private void CheckDatabaseGrowth(long nowUnixMilliseconds)
    {
        // This is warning-only. Large databases can be legitimate, so never VACUUM, delete,
        // or otherwise mutate storage based on size alone.
        long previousCheck = Interlocked.Read(ref lastDatabaseGrowthCheckUnixMilliseconds);
        if (previousCheck != 0 && nowUnixMilliseconds - previousCheck < 60L * 60 * 1000) return;

        long bytes = 0;
        try
        {
            if (File.Exists(dbPath)) bytes += new FileInfo(dbPath).Length;
            string walPath = dbPath + "-wal";
            if (File.Exists(walPath)) bytes += new FileInfo(walPath).Length;
        }
        catch { return; }

        long previousBytes = Interlocked.Exchange(ref lastDatabaseObservedBytes, bytes);
        Interlocked.Exchange(ref lastDatabaseGrowthCheckUnixMilliseconds, nowUnixMilliseconds);
        if (previousBytes <= 0 || bytes <= previousBytes) return;

        long growth = bytes - previousBytes;
        const long minimumGrowthBytes = 512L * 1024 * 1024;
        bool proportionallyLarge = growth >= previousBytes / 4;
        if (growth < minimumGrowthBytes || !proportionallyLarge) return;

        long lastWarning = Interlocked.Read(ref lastDatabaseGrowthWarningUnixMilliseconds);
        if (lastWarning != 0 && nowUnixMilliseconds - lastWarning < 6L * 60 * 60 * 1000) return;
        Interlocked.Exchange(ref lastDatabaseGrowthWarningUnixMilliseconds, nowUnixMilliseconds);

        api.Logger.Warning(
            "[BlockLogger/Storage] Database + WAL grew unusually quickly: {0:0.0} MiB since the previous hourly size sample (now {1:0.0} MiB). This is advisory only; no automatic VACUUM or deletion was performed.",
            growth / 1048576d,
            bytes / 1048576d);
    }

    private void InitializeDatabase()=>WithConnection(conn=>{LoggerDatabaseSchema.Ensure(conn);return 0;});

    private bool WriteMixedBatchWithRetry(
        SqliteConnection conn,
        SqliteCommand eventCmd,
        SqliteCommand aggregateCmd,
        SqliteCommand snapshotCmd,
        List<QueuedEventPayload> genericBatch,
        List<QueuedEntityEventPayload> entityBatch,
        List<EntityActivityAggregate> aggregateBatch,
        List<ContainerSnapshotRecord> snapshotBatch,
        CancellationToken token)
    {
        // Important memory-safety invariant: once a batch is accepted and removed from the
        // bounded producer queues, keep that one batch local to the writer until it succeeds
        // or shutdown cancels it. Never reinsert failed rows into an uncapped retry queue.
        // Under a prolonged SQLite fault, producers therefore remain constrained by the normal
        // queue/overflow caps instead of memory growing with every failed retry cycle.
        int attempt = 0;
        int backoffMs = 100;
        long lastWarningMs = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                writeGate.Wait(token);
                try
                {
                    InsertMixedWriterBatchPrepared(
                        conn, eventCmd, aggregateCmd, snapshotCmd,
                        genericBatch, entityBatch, aggregateBatch, snapshotBatch);
                }
                finally
                {
                    writeGate.Release();
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                attempt++;
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (attempt == 1 || nowMs - lastWarningMs >= 30_000)
                {
                    lastWarningMs = nowMs;
                    api.Logger.Error(
                        "[BlockLogger] SQLite writer batch failed; retaining one bounded batch locally and retrying with backoff. Attempt={0}, batchRows={1}, pending={2}. {3}: {4}",
                        attempt,
                        genericBatch.Count + entityBatch.Count + aggregateBatch.Count + snapshotBatch.Count,
                        PendingEvents + PendingSnapshots + PendingEntityAggregates,
                        ex.GetType().Name,
                        ex.Message);
                }

                if (token.WaitHandle.WaitOne(backoffMs)) return false;
                backoffMs = Math.Min(5000, backoffMs * 2);
            }
        }

        return false;
    }

    private void EventWriterLoop(CancellationToken token)
    {
        using var conn = OpenConnection();
        using var cmd = CreatePreparedEventInsert(conn);
        using var aggregateCmd = CreatePreparedEntityAggregateInsert(conn);
        using var snapshotCmd = CreatePreparedSnapshotInsert(conn);
        var genericBatch = new List<QueuedEventPayload>(2500);
        var entityBatch = new List<QueuedEntityEventPayload>(2500);
        var aggregateBatch = new List<EntityActivityAggregate>(256);
        var snapshotBatch = new List<ContainerSnapshotRecord>(128);
        bool localBatchPending = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Enqueue operations signal this semaphore immediately. When completely idle,
                // use a long safety timeout rather than waking once per flush interval.
                bool idleBeforeWait = PendingEvents <= 0 && entityAggregateQueue.IsEmpty && Interlocked.Read(ref queuedSnapshots) <= 0;
                int waitMilliseconds = idleBeforeWait ? Math.Max(60_000, flushIntervalMs) : flushIntervalMs;
                eventSignal.Wait(TimeSpan.FromMilliseconds(waitMilliseconds), token);
                MaybeScheduleDatabaseBackup(force: false);

                if (PendingEvents < (maxQueue / 2) && PendingEvents > 0)
                    token.WaitHandle.WaitOne(flushIntervalMs);

                do
                {
                    Volatile.Write(ref recoveryWriterInFlight,1);
                    genericBatch.Clear();
                    entityBatch.Clear();
                    aggregateBatch.Clear();
                    snapshotBatch.Clear();
                    var targetBatch = PendingEvents > (maxQueue / 2) ? 2500 : 1000;
                    var criticalTarget = Math.Max(1, targetBatch / 4);
                    var criticalTaken = 0;

                    while (criticalTaken < criticalTarget && criticalEventQueue.TryDequeue(out var critical))
                    {
                        Interlocked.Decrement(ref queuedCriticalEvents);
                        Interlocked.Decrement(ref queuedEvents);
                        genericBatch.Add(critical);
                        criticalTaken++;
                    }
                    while (criticalTaken < criticalTarget && criticalEntityQueue.TryDequeue(out var criticalEntity))
                    {
                        Interlocked.Decrement(ref queuedCriticalEvents);
                        Interlocked.Decrement(ref queuedEvents);
                        entityBatch.Add(criticalEntity);
                        criticalTaken++;
                    }

                    // Emergency-reserve rows are drained ahead of ordinary bulk lifecycle
                    // traffic so a short overload recovers quickly instead of remaining full.
                    while (genericBatch.Count + entityBatch.Count < targetBatch && overflowEventQueue.TryDequeue(out var overflowGeneric))
                    {
                        Interlocked.Decrement(ref queuedOverflowEvents);
                        genericBatch.Add(overflowGeneric);
                    }
                    while (genericBatch.Count + entityBatch.Count < targetBatch && overflowEntityQueue.TryDequeue(out var overflowEntity))
                    {
                        Interlocked.Decrement(ref queuedOverflowEvents);
                        entityBatch.Add(overflowEntity);
                    }

                    while (genericBatch.Count + entityBatch.Count < targetBatch && bulkEntityQueue.TryDequeue(out var bulk))
                    {
                        Interlocked.Decrement(ref queuedBulkEntityEvents);
                        Interlocked.Decrement(ref queuedEvents);
                        entityBatch.Add(bulk);
                    }

                    while (genericBatch.Count + entityBatch.Count < targetBatch && criticalEventQueue.TryDequeue(out var remainder))
                    {
                        Interlocked.Decrement(ref queuedCriticalEvents);
                        Interlocked.Decrement(ref queuedEvents);
                        genericBatch.Add(remainder);
                    }
                    while (genericBatch.Count + entityBatch.Count < targetBatch && criticalEntityQueue.TryDequeue(out var remainderEntity))
                    {
                        Interlocked.Decrement(ref queuedCriticalEvents);
                        Interlocked.Decrement(ref queuedEvents);
                        entityBatch.Add(remainderEntity);
                    }

                    while (aggregateBatch.Count < 256 && entityAggregateQueue.TryDequeue(out var aggregate))
                    {
                        Interlocked.Decrement(ref queuedEntityAggregates);
                        aggregateBatch.Add(aggregate);
                    }

                    while (snapshotBatch.Count < 128 && snapshotQueue.TryDequeue(out var snapshot))
                    {
                        Interlocked.Decrement(ref queuedSnapshots);
                        snapshotBatch.Add(snapshot);
                    }

                    var eventRowCount = genericBatch.Count + entityBatch.Count;
                    var totalRowCount = eventRowCount + aggregateBatch.Count + snapshotBatch.Count;
                    if (totalRowCount > 0)
                    {
                        localBatchPending = true;
                        var sw = Stopwatch.StartNew();
                        bool written = WriteMixedBatchWithRetry(
                            conn,
                            cmd,
                            aggregateCmd,
                            snapshotCmd,
                            genericBatch,
                            entityBatch,
                            aggregateBatch,
                            snapshotBatch,
                            token);
                        sw.Stop();
                        if (!written) break;
                        localBatchPending = false;

                        Interlocked.Add(ref writtenEvents, eventRowCount);
                        Interlocked.Add(ref writtenEntityEvents, entityBatch.Count);
                        Interlocked.Add(ref writtenEntityAggregates, aggregateBatch.Count);
                        Interlocked.Add(ref writtenSnapshots, snapshotBatch.Count);
                        Interlocked.Exchange(ref lastBatchRows, totalRowCount);
                        Interlocked.Exchange(ref lastBatchEntityRows, entityBatch.Count);
                        Interlocked.Exchange(ref lastBatchElapsedMs, sw.ElapsedMilliseconds);
                        Interlocked.Increment(ref writerBatchCount);
                        Interlocked.Add(ref writerBatchTotalMs, sw.ElapsedMilliseconds);
                        long previousMax;
                        do
                        {
                            previousMax = Interlocked.Read(ref writerBatchMaxMs);
                            if (sw.ElapsedMilliseconds <= previousMax) break;
                        }
                        while (Interlocked.CompareExchange(ref writerBatchMaxMs, sw.ElapsedMilliseconds, previousMax) != previousMax);

                        long batchNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        recentBatchMetrics.Enqueue(new StorageBatchMetric(batchNow, totalRowCount, sw.ElapsedMilliseconds));
                        Interlocked.Increment(ref recentBatchMetricCount);
                        while ((Volatile.Read(ref recentBatchMetricCount) > MaxRecentStorageMetrics ||
                                (recentBatchMetrics.TryPeek(out var oldBatch) && oldBatch.UnixMilliseconds < batchNow - 5 * 60_000L)) &&
                               recentBatchMetrics.TryDequeue(out _))
                        {
                            Interlocked.Decrement(ref recentBatchMetricCount);
                        }

                        SuiteTimingRegistry.Record("BlockLogger writer batch", sw.Elapsed.TotalMilliseconds);
                        MarkStorageSuccess();
                    }
                    Volatile.Write(ref recoveryWriterInFlight,0);
                }
                while ((PendingEvents > 0 || !entityAggregateQueue.IsEmpty || Interlocked.Read(ref queuedSnapshots) > 0) && !token.IsCancellationRequested);

                if (!token.IsCancellationRequested) Thread.Sleep(1);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                api.Logger.Error("[BlockLogger] Event storage worker failed: {0}", ex);
                Thread.Sleep(100);
            }
        }

        // If shutdown cancelled a retry while one accepted batch was held locally, flush that
        // bounded batch before reusing/clearing the batch lists in the normal drain helpers.
        // This preserves the old shutdown durability without ever requeueing failed rows into
        // an uncapped collection.
        if (localBatchPending &&
            (genericBatch.Count + entityBatch.Count + aggregateBatch.Count + snapshotBatch.Count) > 0)
        {
            try
            {
                writeGate.Wait();
                try
                {
                    InsertMixedWriterBatchPrepared(
                        conn, cmd, aggregateCmd, snapshotCmd,
                        genericBatch, entityBatch, aggregateBatch, snapshotBatch);
                }
                finally { writeGate.Release(); }

                int eventRows = genericBatch.Count + entityBatch.Count;
                Interlocked.Add(ref writtenEvents, eventRows);
                Interlocked.Add(ref writtenEntityEvents, entityBatch.Count);
                Interlocked.Add(ref writtenEntityAggregates, aggregateBatch.Count);
                Interlocked.Add(ref writtenSnapshots, snapshotBatch.Count);
                MarkStorageSuccess();
                localBatchPending = false;
            }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                api.Logger.Error(
                    "[BlockLogger] Final locally-retained writer batch flush failed; {0} accepted row(s) could not be persisted: {1}: {2}",
                    genericBatch.Count + entityBatch.Count + aggregateBatch.Count + snapshotBatch.Count,
                    ex.GetType().Name,
                    ex.Message);
            }
        }

        DrainEventsPrepared(conn, cmd, aggregateCmd, genericBatch, entityBatch, aggregateBatch);
        DrainSnapshotsPrepared(conn, snapshotCmd, snapshotBatch);
    }

    private void QueryLoop(CancellationToken token)
    {
        try
        {
            migrationTask.GetAwaiter().GetResult();
            using var conn = OpenReadOnlyConnection();
            foreach (var request in queryQueue.GetConsumingEnumerable(token))
            {
                try
                {
                    long queueMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - request.EnqueuedUnixMilliseconds);
                    SuiteTimingRegistry.Record("BlockLogger query queue wait", queueMs);
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        request.Completion.TrySetResult(QueryEvents(conn, request));
                    }
                    finally
                    {
                        sw.Stop();
                        RecordQueryMetric(request.PlayerOnly ? "player-events" : request.EntityOnly ? "entity-events" : "events", sw.ElapsedMilliseconds);
                        SuiteTimingRegistry.Record("BlockLogger query execution", sw.Elapsed.TotalMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    MarkStorageError(ex);
                    api.Logger.Error("[BlockLogger] Event query failed: {0}", ex);
                    request.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MarkStorageError(ex);
            api.Logger.Error("[BlockLogger] Query worker stopped: {0}", ex);
        }
        finally { StopEventQueries(); }
    }

    private void SnapshotQueryLoop(CancellationToken token)
    {
        try
        {
            migrationTask.GetAwaiter().GetResult();
            using var conn = OpenReadOnlyConnection();
            foreach (var request in snapshotQueryQueue.GetConsumingEnumerable(token))
            {
                try
                {
                    long queueMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - request.EnqueuedUnixMilliseconds);
                    SuiteTimingRegistry.Record("BlockLogger snapshot query queue wait", queueMs);
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        request.Completion.TrySetResult(QuerySnapshots(conn, request));
                    }
                    finally
                    {
                        sw.Stop();
                        RecordQueryMetric("container-snapshots", sw.ElapsedMilliseconds);
                        SuiteTimingRegistry.Record("BlockLogger snapshot query execution", sw.Elapsed.TotalMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    MarkStorageError(ex);
                    api.Logger.Error("[BlockLogger] Snapshot query failed: {0}", ex);
                    request.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MarkStorageError(ex);
            api.Logger.Error("[BlockLogger] Snapshot query worker stopped: {0}", ex);
        }
        finally { StopSnapshotQueries(); }
    }

    private bool TryAddQuery<T>(BlockingCollection<T> queue, T request)
    {
        if (Volatile.Read(ref disposeState) != 0) return false;
        try { return !queue.IsAddingCompleted && queue.TryAdd(request); }
        // CompleteAdding/disposal can race admission; callers always receive a finished task.
        catch (InvalidOperationException) { return false; }
    }

    private void StopEventQueries()
    {
        try
        {
            queryQueue.CompleteAdding();
            while (queryQueue.TryTake(out var request))
                request.Completion.TrySetException(new InvalidOperationException("BlockLogger event query worker stopped before this request ran."));
        }
        catch (ObjectDisposedException) { }
    }

    private void StopSnapshotQueries()
    {
        try
        {
            snapshotQueryQueue.CompleteAdding();
            while (snapshotQueryQueue.TryTake(out var request))
                request.Completion.TrySetException(new InvalidOperationException("BlockLogger snapshot query worker stopped before this request ran."));
        }
        catch (ObjectDisposedException) { }
    }

    private List<LogEventRecord> QueryEvents(SqliteConnection conn, EventRequest r)
    {
        if (r.PlayerOnly) return QueryPlayerEvents(conn, r);

        var radius = Math.Max(0, r.Radius);
        var minX = r.X - radius;
        var maxX = r.X + radius;
        var minY = r.Y - radius;
        var maxY = r.Y + radius;
        var minZ = r.Z - radius;
        var maxZ = r.Z + radius;

        var sql = new StringBuilder(@"
SELECT timestamp_ticks, ingame_date, action, player_uid, player_name, world_id,
       dimension, x, y, z, block_code, meta_json
FROM events
WHERE world_id = $worldId
  AND (dimension = $dimension OR dimension = $legacyDimension)
  AND x BETWEEN $minX AND $maxX
  AND y BETWEEN $minY AND $maxY
  AND z BETWEEN $minZ AND $maxZ ");

            if (r.EntityOnly)
                sql.Append("AND action IN (7,8,9,10) ");
            else
                sql.Append("AND action IN (1,2,3,4,5,6) ");

            if (!string.IsNullOrWhiteSpace(r.Tag))
            {
                var tag = r.Tag.Trim();
                if (tag.Equals("block", StringComparison.OrdinalIgnoreCase))
                    sql.Append("AND action IN (1,2,3) ");
                else if (tag.Equals("container", StringComparison.OrdinalIgnoreCase))
                    sql.Append("AND action IN (4,5,6) ");
                else if (tag.Equals("entity", StringComparison.OrdinalIgnoreCase))
                    sql.Append("AND action IN (7,8,9,10) ");
                else if (tag.Equals("item", StringComparison.OrdinalIgnoreCase))
                    sql.Append("AND (meta_json LIKE '%itemCode%' OR meta_json LIKE '%heldItem%') ");
                else
                {
                    sql.Append("AND action = $tagAction ");
                }
            }

            if (!string.IsNullOrWhiteSpace(r.Text))
            {
                sql.Append(@"AND (
 player_name LIKE $text COLLATE NOCASE OR
 player_uid LIKE $text COLLATE NOCASE OR
 block_code LIKE $text COLLATE NOCASE OR
 ingame_date LIKE $text COLLATE NOCASE OR
 meta_json LIKE $text COLLATE NOCASE
) ");
            }

            sql.Append(@"AND ((x - $cx) * (x - $cx) + (y - $cy) * (y - $cy) + (z - $cz) * (z - $cz)) <= $radiusSq ");
            sql.Append("ORDER BY timestamp_ticks DESC LIMIT $limit OFFSET $offset;");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddWithValue("$worldId", currentWorldId);
            cmd.Parameters.AddWithValue("$dimension", r.Dimension.ToString());
            cmd.Parameters.AddWithValue("$legacyDimension", LegacyDimensionName(r.Dimension));
            cmd.Parameters.AddWithValue("$minX", minX);
            cmd.Parameters.AddWithValue("$maxX", maxX);
            cmd.Parameters.AddWithValue("$minY", minY);
            cmd.Parameters.AddWithValue("$maxY", maxY);
            cmd.Parameters.AddWithValue("$minZ", minZ);
            cmd.Parameters.AddWithValue("$maxZ", maxZ);
            cmd.Parameters.AddWithValue("$cx", r.X);
            cmd.Parameters.AddWithValue("$cy", r.Y);
            cmd.Parameters.AddWithValue("$cz", r.Z);
            cmd.Parameters.AddWithValue("$radiusSq", radius * radius);
            int pageLimit = Math.Clamp(r.MaxCount, 1, 200);
            int fetchLimit = r.EntityOnly ? Math.Min(2200, pageLimit + Math.Clamp(r.Offset, 0, 2000)) : pageLimit;
            cmd.Parameters.AddWithValue("$limit", fetchLimit);
            cmd.Parameters.AddWithValue("$offset", r.EntityOnly ? 0 : Math.Clamp(r.Offset, 0, 2000));

            if (!string.IsNullOrWhiteSpace(r.Text))
                cmd.Parameters.AddWithValue("$text", "%" + r.Text.Trim() + "%");

            if (!string.IsNullOrWhiteSpace(r.Tag) &&
                !r.Tag.Equals("block", StringComparison.OrdinalIgnoreCase) &&
                !r.Tag.Equals("container", StringComparison.OrdinalIgnoreCase) &&
                !r.Tag.Equals("entity", StringComparison.OrdinalIgnoreCase) &&
                !r.Tag.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseActionTag(r.Tag, out var action))
                    cmd.Parameters.AddWithValue("$tagAction", (int)action);
                else
                    return new List<LogEventRecord>();
            }

            using var reader = cmd.ExecuteReader();
            var results = new List<LogEventRecord>();
            while (reader.Read())
                results.Add(ReadEvent(reader));
            reader.Close();

            if (r.EntityOnly)
            {
                results.AddRange(QueryEntityActivity(conn, r, minX, maxX, minY, maxY, minZ, maxZ, radius));
                return results
                    .OrderByDescending(x => x.TimestampUtc.Ticks)
                    .Skip(Math.Clamp(r.Offset, 0, 2000))
                    .Take(Math.Clamp(r.MaxCount, 1, 200))
                    .ToList();
            }

        return results;
    }

    private List<LogEventRecord> QueryPlayerEvents(SqliteConnection conn, EventRequest r)
    {
        var player = (r.Player ?? string.Empty).Trim();
        if (player.Length == 0) return new List<LogEventRecord>();

        var sql = new StringBuilder(@"
SELECT timestamp_ticks, ingame_date, action, player_uid, player_name, world_id,
       dimension, x, y, z, block_code, meta_json
FROM events
WHERE world_id = $worldId
  AND player_uid <> ''
  AND " + (r.ExactUid ? "player_uid = $player COLLATE BINARY " : @"(
      player_uid = $player COLLATE NOCASE OR
      player_name = $player COLLATE NOCASE OR
      player_name LIKE $playerPrefix COLLATE NOCASE
  ) "));

            var category = r.Tag?.Trim().ToLowerInvariant();
            if (category == "blocks")
                sql.Append("AND action IN (1,2,3) ");
            else if (category == "containers")
                sql.Append("AND action IN (4,5,6) ");
            else if (category == "entities")
                sql.Append("AND action IN (7,8,9,10) ");

            sql.Append("ORDER BY timestamp_ticks DESC LIMIT $limit OFFSET $offset;");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddWithValue("$worldId", currentWorldId);
            cmd.Parameters.AddWithValue("$player", player);
            if (!r.ExactUid) cmd.Parameters.AddWithValue("$playerPrefix", player + "%");
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(r.MaxCount, 1, 200));
            cmd.Parameters.AddWithValue("$offset", Math.Clamp(r.Offset, 0, 2000));

            using var reader = cmd.ExecuteReader();
            var results = new List<LogEventRecord>();
            while (reader.Read())
                results.Add(ReadEvent(reader));

        return results;
    }

    private List<LogEventRecord> QueryEntityActivity(SqliteConnection conn, EventRequest r, int minX, int maxX, int minY, int maxY, int minZ, int maxZ, int radius)
    {
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder(@"
SELECT window_end_ticks, action, world_id, dimension, sample_x, sample_y, sample_z,
       entity_code, entity_class, event_count, region_x, region_z
FROM entity_activity
WHERE world_id = $worldId
  AND (dimension = $dimension OR dimension = $legacyDimension)
  AND sample_x BETWEEN $minX AND $maxX
  AND sample_y BETWEEN $minY AND $maxY
  AND sample_z BETWEEN $minZ AND $maxZ
  AND ((sample_x - $cx) * (sample_x - $cx) + (sample_y - $cy) * (sample_y - $cy) + (sample_z - $cz) * (sample_z - $cz)) <= $radiusSq ");

        if (!string.IsNullOrWhiteSpace(r.Text))
            sql.Append("AND (entity_code LIKE $text COLLATE NOCASE OR entity_class LIKE $text COLLATE NOCASE) ");

        int? aggregateActionFilter = null;
        if (!string.IsNullOrWhiteSpace(r.Tag))
        {
            if (r.Tag.Equals("entity", StringComparison.OrdinalIgnoreCase)) { }
            else if (TryParseActionTag(r.Tag, out var parsedAction) && (parsedAction is LogActionType.EntitySpawn or LogActionType.EntityDespawn))
            {
                aggregateActionFilter = (int)parsedAction;
                sql.Append("AND action = $action ");
            }
            else return new List<LogEventRecord>();
        }

        sql.Append("ORDER BY window_end_ticks DESC LIMIT $limit;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$worldId", currentWorldId);
        cmd.Parameters.AddWithValue("$dimension", r.Dimension.ToString());
        cmd.Parameters.AddWithValue("$legacyDimension", LegacyDimensionName(r.Dimension));
        cmd.Parameters.AddWithValue("$minX", minX); cmd.Parameters.AddWithValue("$maxX", maxX);
        cmd.Parameters.AddWithValue("$minY", minY); cmd.Parameters.AddWithValue("$maxY", maxY);
        cmd.Parameters.AddWithValue("$minZ", minZ); cmd.Parameters.AddWithValue("$maxZ", maxZ);
        cmd.Parameters.AddWithValue("$cx", r.X); cmd.Parameters.AddWithValue("$cy", r.Y); cmd.Parameters.AddWithValue("$cz", r.Z);
        cmd.Parameters.AddWithValue("$radiusSq", radius * radius);
        cmd.Parameters.AddWithValue("$limit", Math.Min(2200, Math.Clamp(r.MaxCount, 1, 200) + Math.Clamp(r.Offset, 0, 2000)));
        if (!string.IsNullOrWhiteSpace(r.Text)) cmd.Parameters.AddWithValue("$text", "%" + r.Text.Trim() + "%");
        if (aggregateActionFilter.HasValue) cmd.Parameters.AddWithValue("$action", aggregateActionFilter.Value);

        var results = new List<LogEventRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rec = new LogEventRecord
            {
                TimestampUtc = new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
                IngameDate = "aggregate",
                Action = (LogActionType)reader.GetInt32(1),
                PlayerUid = "", PlayerName = "System",
                WorldId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Dimension = reader.IsDBNull(3) ? "" : reader.GetString(3),
                X = reader.GetInt32(4), Y = reader.GetInt32(5), Z = reader.GetInt32(6),
                BlockCode = ""
            };
            rec.Meta["entityCode"] = reader.IsDBNull(7) ? "entity" : reader.GetString(7);
            rec.Meta["entityClass"] = reader.IsDBNull(8) ? "" : reader.GetString(8);
            rec.Meta["eventCount"] = reader.GetInt64(9).ToString();
            rec.Meta["aggregate"] = "true";
            rec.Meta["regionX"] = reader.GetInt32(10).ToString();
            rec.Meta["regionZ"] = reader.GetInt32(11).ToString();
            results.Add(rec);
        }
        return results;
    }

    private List<ContainerSnapshotRecord> QuerySnapshots(SqliteConnection conn, SnapshotRequest r)
    {
        var radius = Math.Max(0, r.Radius);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT timestamp_ticks, ingame_date, world_id, dimension, x, y, z, block_code,
       player_uid, player_name, inventory_id, reason, changed_slot_id,
       slot_count, non_empty_slot_count, total_item_count, slots_json
FROM snapshots
WHERE world_id = $worldId
  AND (dimension = $dimension OR dimension = $legacyDimension)
  AND x BETWEEN $minX AND $maxX
  AND y BETWEEN $minY AND $maxY
  AND z BETWEEN $minZ AND $maxZ
  AND ((x - $cx) * (x - $cx) + (y - $cy) * (y - $cy) + (z - $cz) * (z - $cz)) <= $radiusSq
  AND (
    $text = '' OR player_name LIKE $text COLLATE NOCASE OR
    player_uid LIKE $text COLLATE NOCASE OR block_code LIKE $text COLLATE NOCASE OR
    reason LIKE $text COLLATE NOCASE OR inventory_id LIKE $text COLLATE NOCASE
  )
ORDER BY timestamp_ticks DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$worldId", currentWorldId);
            cmd.Parameters.AddWithValue("$dimension", r.Dimension.ToString());
            cmd.Parameters.AddWithValue("$legacyDimension", LegacyDimensionName(r.Dimension));
            cmd.Parameters.AddWithValue("$minX", r.X - radius);
            cmd.Parameters.AddWithValue("$maxX", r.X + radius);
            cmd.Parameters.AddWithValue("$minY", r.Y - radius);
            cmd.Parameters.AddWithValue("$maxY", r.Y + radius);
            cmd.Parameters.AddWithValue("$minZ", r.Z - radius);
            cmd.Parameters.AddWithValue("$maxZ", r.Z + radius);
            cmd.Parameters.AddWithValue("$cx", r.X);
            cmd.Parameters.AddWithValue("$cy", r.Y);
            cmd.Parameters.AddWithValue("$cz", r.Z);
            cmd.Parameters.AddWithValue("$radiusSq", radius * radius);
            cmd.Parameters.AddWithValue("$text", string.IsNullOrWhiteSpace(r.Text) ? "" : "%" + r.Text.Trim() + "%");
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(r.MaxCount, 1, 100));

            using var reader = cmd.ExecuteReader();
            var results = new List<ContainerSnapshotRecord>();
            while (reader.Read())
                results.Add(ReadSnapshot(reader));

        return results;
    }

    private void InsertEvents(List<LogEventRecord> records)
    {
        using var conn = OpenConnection();
        using var cmd = CreatePreparedEventInsert(conn);
        InsertEventsPrepared(conn, cmd, records);
    }

    private void InsertSnapshots(List<ContainerSnapshotRecord> records)
    {
        using var conn = OpenConnection();
        using var cmd = CreatePreparedSnapshotInsert(conn);
        InsertSnapshotsPrepared(conn, cmd, records);
    }

    private static SqliteCommand CreatePreparedEventInsert(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO events
(timestamp_ticks,timestamp_utc,ingame_date,action,player_uid,player_name,world_id,dimension,x,y,z,block_code,meta_json)
VALUES
($ticks,$utc,$ingame,$action,$uid,$name,$world,$dimension,$x,$y,$z,$block,$meta);";
        cmd.Parameters.Add("$ticks", SqliteType.Integer);
        cmd.Parameters.Add("$utc", SqliteType.Text);
        cmd.Parameters.Add("$ingame", SqliteType.Text);
        cmd.Parameters.Add("$action", SqliteType.Integer);
        cmd.Parameters.Add("$uid", SqliteType.Text);
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$world", SqliteType.Text);
        cmd.Parameters.Add("$dimension", SqliteType.Text);
        cmd.Parameters.Add("$x", SqliteType.Integer);
        cmd.Parameters.Add("$y", SqliteType.Integer);
        cmd.Parameters.Add("$z", SqliteType.Integer);
        cmd.Parameters.Add("$block", SqliteType.Text);
        cmd.Parameters.Add("$meta", SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    private static SqliteCommand CreatePreparedEntityAggregateInsert(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO entity_activity
(window_start_ticks,window_end_ticks,action,entity_type_id,entity_code,entity_class,event_count,world_id,dimension,sample_x,sample_y,sample_z,region_x,region_z)
VALUES ($start,$end,$action,$type,$code,$class,$count,$world,$dimension,$x,$y,$z,$regionX,$regionZ);";
        cmd.Parameters.Add("$start", SqliteType.Integer);
        cmd.Parameters.Add("$end", SqliteType.Integer);
        cmd.Parameters.Add("$action", SqliteType.Integer);
        cmd.Parameters.Add("$type", SqliteType.Integer);
        cmd.Parameters.Add("$code", SqliteType.Text);
        cmd.Parameters.Add("$class", SqliteType.Text);
        cmd.Parameters.Add("$count", SqliteType.Integer);
        cmd.Parameters.Add("$world", SqliteType.Text);
        cmd.Parameters.Add("$dimension", SqliteType.Text);
        cmd.Parameters.Add("$x", SqliteType.Integer);
        cmd.Parameters.Add("$y", SqliteType.Integer);
        cmd.Parameters.Add("$z", SqliteType.Integer);
        cmd.Parameters.Add("$regionX", SqliteType.Integer);
        cmd.Parameters.Add("$regionZ", SqliteType.Integer);
        cmd.Prepare();
        return cmd;
    }

    private static void InsertEntityAggregatesPrepared(SqliteConnection conn, SqliteCommand cmd, List<EntityActivityAggregate> records)
    {
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        try
        {
            foreach (var r in records)
            {
                SetEntityAggregateParameters(cmd, r);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        finally { cmd.Transaction = null; }
    }

    private static void SetEntityAggregateParameters(SqliteCommand cmd, in EntityActivityAggregate r)
    {
        cmd.Parameters["$start"].Value = r.WindowStartTicks;
        cmd.Parameters["$end"].Value = r.WindowEndTicks;
        cmd.Parameters["$action"].Value = r.Action;
        cmd.Parameters["$type"].Value = r.EntityTypeId;
        cmd.Parameters["$code"].Value = r.EntityCode ?? string.Empty;
        cmd.Parameters["$class"].Value = r.EntityClass ?? string.Empty;
        cmd.Parameters["$count"].Value = r.EventCount;
        cmd.Parameters["$world"].Value = r.WorldId ?? string.Empty;
        cmd.Parameters["$dimension"].Value = r.Dimension ?? string.Empty;
        cmd.Parameters["$x"].Value = r.SampleX;
        cmd.Parameters["$y"].Value = r.SampleY;
        cmd.Parameters["$z"].Value = r.SampleZ;
        cmd.Parameters["$regionX"].Value = r.RegionX;
        cmd.Parameters["$regionZ"].Value = r.RegionZ;
    }

    private static SqliteCommand CreatePreparedSnapshotInsert(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO snapshots
(timestamp_ticks,timestamp_utc,ingame_date,world_id,dimension,x,y,z,block_code,
 player_uid,player_name,inventory_id,reason,changed_slot_id,slot_count,
 non_empty_slot_count,total_item_count,slots_json)
VALUES
($ticks,$utc,$ingame,$world,$dimension,$x,$y,$z,$block,$uid,$name,$inv,$reason,
 $slot,$slotCount,$nonEmpty,$total,$slots);";
        cmd.Parameters.Add("$ticks", SqliteType.Integer);
        cmd.Parameters.Add("$utc", SqliteType.Text);
        cmd.Parameters.Add("$ingame", SqliteType.Text);
        cmd.Parameters.Add("$world", SqliteType.Text);
        cmd.Parameters.Add("$dimension", SqliteType.Text);
        cmd.Parameters.Add("$x", SqliteType.Integer);
        cmd.Parameters.Add("$y", SqliteType.Integer);
        cmd.Parameters.Add("$z", SqliteType.Integer);
        cmd.Parameters.Add("$block", SqliteType.Text);
        cmd.Parameters.Add("$uid", SqliteType.Text);
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$inv", SqliteType.Text);
        cmd.Parameters.Add("$reason", SqliteType.Text);
        cmd.Parameters.Add("$slot", SqliteType.Integer);
        cmd.Parameters.Add("$slotCount", SqliteType.Integer);
        cmd.Parameters.Add("$nonEmpty", SqliteType.Integer);
        cmd.Parameters.Add("$total", SqliteType.Integer);
        cmd.Parameters.Add("$slots", SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    private static void InsertMixedEventsPrepared(
        SqliteConnection conn,
        SqliteCommand cmd,
        List<QueuedEventPayload> genericRecords,
        List<QueuedEntityEventPayload> entityRecords)
    {
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;

        foreach (var r in genericRecords)
        {
            SetQueuedEventParameters(cmd, r);
            cmd.ExecuteNonQuery();
        }

        foreach (var r in entityRecords)
        {
            SetQueuedEntityEventParameters(cmd, r);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        cmd.Transaction = null;
    }

    private static void InsertMixedWriterBatchPrepared(
        SqliteConnection conn,
        SqliteCommand eventCmd,
        SqliteCommand aggregateCmd,
        SqliteCommand snapshotCmd,
        List<QueuedEventPayload> genericRecords,
        List<QueuedEntityEventPayload> entityRecords,
        List<EntityActivityAggregate> aggregateRecords,
        List<ContainerSnapshotRecord> snapshotRecords)
    {
        using var tx = conn.BeginTransaction();
        eventCmd.Transaction = tx;
        aggregateCmd.Transaction = tx;
        snapshotCmd.Transaction = tx;
        try
        {
            foreach (var r in genericRecords)
            {
                SetQueuedEventParameters(eventCmd, r);
                eventCmd.ExecuteNonQuery();
            }

            foreach (var r in entityRecords)
            {
                SetQueuedEntityEventParameters(eventCmd, r);
                eventCmd.ExecuteNonQuery();
            }

            foreach (var r in aggregateRecords)
            {
                SetEntityAggregateParameters(aggregateCmd, r);
                aggregateCmd.ExecuteNonQuery();
            }

            foreach (var r in snapshotRecords)
            {
                SetSnapshotParameters(snapshotCmd, r);
                snapshotCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            eventCmd.Transaction = null;
            aggregateCmd.Transaction = null;
            snapshotCmd.Transaction = null;
        }
    }

    private static void InsertQueuedEventsPrepared(SqliteConnection conn, SqliteCommand cmd, List<QueuedEventPayload> records)
    {
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var r in records)
        {
            SetQueuedEventParameters(cmd, r);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        cmd.Transaction = null;
    }

    private static void SetQueuedEntityEventParameters(SqliteCommand cmd, in QueuedEntityEventPayload r)
    {
        cmd.Parameters["$ticks"].Value = r.TimestampTicks;
        cmd.Parameters["$utc"].Value = string.Empty;
        cmd.Parameters["$ingame"].Value = r.IngameDate ?? "";
        cmd.Parameters["$action"].Value = (int)r.Action;
        cmd.Parameters["$uid"].Value = r.PlayerUid ?? "";
        cmd.Parameters["$name"].Value = r.PlayerName ?? "";
        cmd.Parameters["$world"].Value = r.WorldId ?? "";
        cmd.Parameters["$dimension"].Value = r.Dimension ?? "";
        cmd.Parameters["$x"].Value = r.X;
        cmd.Parameters["$y"].Value = r.Y;
        cmd.Parameters["$z"].Value = r.Z;
        cmd.Parameters["$block"].Value = string.Empty;
        cmd.Parameters["$meta"].Value = BuildEntityMetaJson(r);
    }

    private static string BuildEntityMetaJson(in QueuedEntityEventPayload r)
    {
        var sb = new StringBuilder(192);
        sb.Append('{');
        var first = true;
        AppendMetaPair(sb, ref first, "entityId", r.EntityId.ToString());
        AppendMetaPair(sb, ref first, "entityTypeId", r.EntityTypeId.ToString());
        AppendMetaPair(sb, ref first, "entityCode", r.EntityCode);
        AppendMetaPair(sb, ref first, "entityClass", r.EntityClass);
        if (r.EventCount > 1) AppendMetaPair(sb, ref first, "eventCount", r.EventCount.ToString());
        if (!string.IsNullOrEmpty(r.HeldItem))
        {
            AppendMetaPair(sb, ref first, "heldItem", r.HeldItem);
            AppendMetaPair(sb, ref first, "heldStackSize", r.HeldStackSize.ToString());
        }
        if (r.DamageTier != int.MinValue)
            AppendMetaPair(sb, ref first, "damageTier", r.DamageTier.ToString());
        if (!string.IsNullOrEmpty(r.DamageType))
            AppendMetaPair(sb, ref first, "damageType", r.DamageType);
        if (r.SourceEntityId >= 0)
            AppendMetaPair(sb, ref first, "sourceEntityId", r.SourceEntityId.ToString());
        if (!string.IsNullOrEmpty(r.SourceEntityCode))
            AppendMetaPair(sb, ref first, "sourceEntityCode", r.SourceEntityCode);
        if (!string.IsNullOrEmpty(r.SourcePlayerUid))
            AppendMetaPair(sb, ref first, "sourcePlayerUid", r.SourcePlayerUid);
        if (!string.IsNullOrEmpty(r.SourcePlayerName))
            AppendMetaPair(sb, ref first, "sourcePlayerName", r.SourcePlayerName);
        if (r.SourceX.HasValue && r.SourceY.HasValue && r.SourceZ.HasValue)
            AppendMetaPair(sb, ref first, "sourcePos", $"{r.SourceX.Value:0.##},{r.SourceY.Value:0.##},{r.SourceZ.Value:0.##}");
        if (!string.IsNullOrEmpty(r.DespawnReason))
            AppendMetaPair(sb, ref first, "despawnReason", r.DespawnReason);
        sb.Append('}');
        return sb.ToString();
    }

    private static void SetQueuedEventParameters(SqliteCommand cmd, in QueuedEventPayload r)
    {
        cmd.Parameters["$ticks"].Value = r.TimestampUtc.Ticks;
        cmd.Parameters["$utc"].Value = string.Empty;
        cmd.Parameters["$ingame"].Value = r.IngameDate ?? "";
        cmd.Parameters["$action"].Value = (int)r.Action;
        cmd.Parameters["$uid"].Value = r.PlayerUid ?? "";
        cmd.Parameters["$name"].Value = r.PlayerName ?? "";
        cmd.Parameters["$world"].Value = r.WorldId ?? "";
        cmd.Parameters["$dimension"].Value = r.Dimension ?? "";
        cmd.Parameters["$x"].Value = r.X;
        cmd.Parameters["$y"].Value = r.Y;
        cmd.Parameters["$z"].Value = r.Z;
        cmd.Parameters["$block"].Value = r.BlockCode ?? "";

        // Build the tiny metadata object directly. The old path allocated a Dictionary and
        // invoked Newtonsoft for every event, which created heavy GC pressure in entity storms.
        cmd.Parameters["$meta"].Value = BuildMetaJson(r);
    }

    private static string BuildMetaJson(in QueuedEventPayload r)
    {
        if (r.K1 == null && r.K2 == null && r.K3 == null && r.K4 == null &&
            r.K5 == null && r.K6 == null && r.K7 == null && r.K8 == null) return "{}";

        var sb = new StringBuilder(160);
        sb.Append('{');
        var first = true;
        AppendMetaPair(sb, ref first, r.K1, r.V1); AppendMetaPair(sb, ref first, r.K2, r.V2);
        AppendMetaPair(sb, ref first, r.K3, r.V3); AppendMetaPair(sb, ref first, r.K4, r.V4);
        AppendMetaPair(sb, ref first, r.K5, r.V5); AppendMetaPair(sb, ref first, r.K6, r.V6);
        AppendMetaPair(sb, ref first, r.K7, r.V7); AppendMetaPair(sb, ref first, r.K8, r.V8);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendMetaPair(StringBuilder sb, ref bool first, string? key, string? value)
    {
        if (string.IsNullOrEmpty(key) || value == null) return;
        if (!first) sb.Append(',');
        first = false;
        AppendJsonString(sb, key);
        sb.Append(':');
        AppendJsonString(sb, value);
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void InsertEventsPrepared(SqliteConnection conn, SqliteCommand cmd, List<LogEventRecord> records)
    {
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var r in records)
        {
            SetEventParameters(cmd, r);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        cmd.Transaction = null;
    }

    private static void InsertSnapshotsPrepared(SqliteConnection conn, SqliteCommand cmd, List<ContainerSnapshotRecord> records)
    {
        using var tx = conn.BeginTransaction();
        cmd.Transaction = tx;
        foreach (var r in records)
        {
            SetSnapshotParameters(cmd, r);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        cmd.Transaction = null;
    }

    private static void SetEventParameters(SqliteCommand cmd, LogEventRecord r)
    {
        cmd.Parameters["$ticks"].Value = r.TimestampUtc.Ticks;
        cmd.Parameters["$utc"].Value = string.Empty;
        cmd.Parameters["$ingame"].Value = r.IngameDate ?? "";
        cmd.Parameters["$action"].Value = (int)r.Action;
        cmd.Parameters["$uid"].Value = r.PlayerUid ?? "";
        cmd.Parameters["$name"].Value = r.PlayerName ?? "";
        cmd.Parameters["$world"].Value = r.WorldId ?? "";
        cmd.Parameters["$dimension"].Value = r.Dimension ?? "";
        cmd.Parameters["$x"].Value = r.X;
        cmd.Parameters["$y"].Value = r.Y;
        cmd.Parameters["$z"].Value = r.Z;
        cmd.Parameters["$block"].Value = r.BlockCode ?? "";
        cmd.Parameters["$meta"].Value = JsonConvert.SerializeObject(r.Meta);
    }

    private static void SetSnapshotParameters(SqliteCommand cmd, ContainerSnapshotRecord r)
    {
        cmd.Parameters["$ticks"].Value = r.TimestampUtc.Ticks;
        cmd.Parameters["$utc"].Value = string.Empty;
        cmd.Parameters["$ingame"].Value = r.IngameDate ?? "";
        cmd.Parameters["$world"].Value = r.WorldId ?? "";
        cmd.Parameters["$dimension"].Value = r.Dimension ?? "";
        cmd.Parameters["$x"].Value = r.X;
        cmd.Parameters["$y"].Value = r.Y;
        cmd.Parameters["$z"].Value = r.Z;
        cmd.Parameters["$block"].Value = r.BlockCode ?? "";
        cmd.Parameters["$uid"].Value = r.PlayerUid ?? "";
        cmd.Parameters["$name"].Value = r.PlayerName ?? "";
        cmd.Parameters["$inv"].Value = r.InventoryId ?? "";
        cmd.Parameters["$reason"].Value = r.Reason ?? "";
        cmd.Parameters["$slot"].Value = (object?)r.ChangedSlotId ?? DBNull.Value;
        cmd.Parameters["$slotCount"].Value = r.SlotCount;
        cmd.Parameters["$nonEmpty"].Value = r.NonEmptySlotCount;
        cmd.Parameters["$total"].Value = r.TotalItemCount;
        cmd.Parameters["$slots"].Value = JsonConvert.SerializeObject(r.Slots);
    }

    private void MigrateLegacyJsonlSafe(CancellationToken token)
    {
        try
        {
            MigrateLegacyJsonl(token);
            migrationComplete = true;
            api.Logger.Notification("[BlockLogger] Storage migration/index is ready.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MarkStorageError(ex);
            api.Logger.Error("[BlockLogger] Legacy log migration failed: {0}", ex);
            migrationComplete = true; // Do not hold the server hostage.
        }
    }

    private void MigrateLegacyJsonl(CancellationToken token)
    {
        WithConnection(conn =>
        {
            var files = Directory.Exists(eventLogDir)
                ? Directory.EnumerateFiles(eventLogDir, "*.jsonl").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (IsImported(conn, "event:" + file)) continue;

                var batch = new List<LogEventRecord>(500);
                foreach (var line in File.ReadLines(file))
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonConvert.DeserializeObject<LogEventRecord>(line);
                        if (r != null) batch.Add(r);
                        if (batch.Count >= 500)
                        {
                            ExecuteMigrationEventBatch(conn, batch);
                            batch.Clear();
                        }
                    }
                    catch { /* Ignore one corrupt legacy line, keep migrating. */ }
                }

                if (batch.Count > 0) ExecuteMigrationEventBatch(conn, batch);
                MarkImported(conn, "event:" + file);
            }

            var snapshotFiles = Directory.Exists(snapshotLogDir)
                ? Directory.EnumerateFiles(snapshotLogDir, "*.jsonl").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            foreach (var file in snapshotFiles)
            {
                token.ThrowIfCancellationRequested();
                if (IsImported(conn, "snapshot:" + file)) continue;

                var batch = new List<ContainerSnapshotRecord>(100);
                foreach (var line in File.ReadLines(file))
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var r = JsonConvert.DeserializeObject<ContainerSnapshotRecord>(line);
                        if (r != null) batch.Add(r);
                        if (batch.Count >= 100)
                        {
                            ExecuteMigrationSnapshotBatch(conn, batch);
                            batch.Clear();
                        }
                    }
                    catch { }
                }

                if (batch.Count > 0) ExecuteMigrationSnapshotBatch(conn, batch);
                MarkImported(conn, "snapshot:" + file);
            }

            return 0;
        });
    }

    private static bool IsImported(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM meta WHERE key=$key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() != null;
    }

    private void MarkImported(SqliteConnection conn, string key)
    {
        writeGate.Wait(cts.Token);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES($key,$value);";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", DateTime.UtcNow.Ticks.ToString());
            cmd.ExecuteNonQuery();
        }
        finally { writeGate.Release(); }
    }

    private void ExecuteMigrationEventBatch(SqliteConnection conn, List<LogEventRecord> records)
    {
        writeGate.Wait(cts.Token);
        try { InsertEventsUsingConnection(conn, records); }
        finally { writeGate.Release(); }
        // Yield between legacy batches so live event writes remain responsive.
        Thread.Sleep(1);
    }

    private void ExecuteMigrationSnapshotBatch(SqliteConnection conn, List<ContainerSnapshotRecord> records)
    {
        writeGate.Wait(cts.Token);
        try { InsertSnapshotsUsingConnection(conn, records); }
        finally { writeGate.Release(); }
        Thread.Sleep(1);
    }

    private static void InsertEventsUsingConnection(SqliteConnection conn, List<LogEventRecord> records)
    {
        using var cmd = CreatePreparedEventInsert(conn);
        InsertEventsPrepared(conn, cmd, records);
    }

    private static void InsertSnapshotsUsingConnection(SqliteConnection conn, List<ContainerSnapshotRecord> records)
    {
        using var cmd = CreatePreparedSnapshotInsert(conn);
        InsertSnapshotsPrepared(conn, cmd, records);
    }

    private static string LegacyDimensionName(int dimension) => dimension switch
    {
        0 => "overworld",
        1 => "alternate1",
        2 => "alternate2",
        _ => $"dimension-{dimension}"
    };

    private static bool TryParseActionTag(string value, out LogActionType action)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "break": action = LogActionType.BlockBreak; return true;
            case "place": action = LogActionType.BlockPlace; return true;
            case "interact":
            case "use": action = LogActionType.BlockUse; return true;
            case "open": action = LogActionType.ContainerOpen; return true;
            case "take": action = LogActionType.ContainerTake; return true;
            case "put": action = LogActionType.ContainerPut; return true;
            case "entityinteract": action = LogActionType.EntityInteract; return true;
            case "entityspawn": action = LogActionType.EntitySpawn; return true;
            case "entitydeath": action = LogActionType.EntityDeath; return true;
            case "entitydespawn": action = LogActionType.EntityDespawn; return true;
            default:
                return Enum.TryParse(value, true, out action);
        }
    }

    private static LogEventRecord ReadEvent(SqliteDataReader reader)
    {
        var r = new LogEventRecord
        {
            TimestampUtc = new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
            IngameDate = reader.IsDBNull(1) ? "" : reader.GetString(1),
            Action = (LogActionType)reader.GetInt32(2),
            PlayerUid = reader.IsDBNull(3) ? "" : reader.GetString(3),
            PlayerName = reader.IsDBNull(4) ? "" : reader.GetString(4),
            WorldId = reader.IsDBNull(5) ? "" : reader.GetString(5),
            Dimension = reader.IsDBNull(6) ? "" : reader.GetString(6),
            X = reader.GetInt32(7),
            Y = reader.GetInt32(8),
            Z = reader.GetInt32(9),
            BlockCode = reader.IsDBNull(10) ? "" : reader.GetString(10)
        };
        if (!reader.IsDBNull(11))
        {
            try { r.Meta = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.GetString(11)) ?? new(); }
            catch { }
        }
        return r;
    }

    private static ContainerSnapshotRecord ReadSnapshot(SqliteDataReader reader)
    {
        var r = new ContainerSnapshotRecord
        {
            TimestampUtc = new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
            IngameDate = reader.IsDBNull(1) ? "" : reader.GetString(1),
            WorldId = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Dimension = reader.IsDBNull(3) ? "" : reader.GetString(3),
            X = reader.GetInt32(4),
            Y = reader.GetInt32(5),
            Z = reader.GetInt32(6),
            BlockCode = reader.IsDBNull(7) ? "" : reader.GetString(7),
            PlayerUid = reader.IsDBNull(8) ? "" : reader.GetString(8),
            PlayerName = reader.IsDBNull(9) ? "" : reader.GetString(9),
            InventoryId = reader.IsDBNull(10) ? "" : reader.GetString(10),
            Reason = reader.IsDBNull(11) ? "" : reader.GetString(11),
            ChangedSlotId = reader.IsDBNull(12) ? null : reader.GetInt32(12),
            SlotCount = reader.GetInt32(13),
            NonEmptySlotCount = reader.GetInt32(14),
            TotalItemCount = reader.GetInt32(15)
        };
        if (!reader.IsDBNull(16))
        {
            try { r.Slots = JsonConvert.DeserializeObject<List<ContainerSnapshotSlot>>(reader.GetString(16)) ?? new(); }
            catch { }
        }
        return r;
    }

    private static void ObservePeak(ref long peak, long value)
    {
        var current = Interlocked.Read(ref peak);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref peak, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE; PRAGMA cache_size=-16384; PRAGMA mmap_size=0; PRAGMA wal_autocheckpoint=2000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Private;Pooling=False");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON; PRAGMA temp_store=FILE; PRAGMA cache_size=-8192; PRAGMA mmap_size=0;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private T WithConnection<T>(System.Func<SqliteConnection, T> action)
    {
        using var conn = OpenConnection();
        return action(conn);
    }

    private T WithReadOnlyConnection<T>(System.Func<SqliteConnection, T> action)
    {
        using var conn = OpenReadOnlyConnection();
        return action(conn);
    }

    private void DrainEventsPrepared(
        SqliteConnection conn,
        SqliteCommand cmd,
        SqliteCommand aggregateCmd,
        List<QueuedEventPayload> genericBatch,
        List<QueuedEntityEventPayload> entityBatch,
        List<EntityActivityAggregate> aggregateBatch)
    {
        while (true)
        {
            genericBatch.Clear();
            entityBatch.Clear();
            while (genericBatch.Count + entityBatch.Count < 5000)
            {
                if (criticalEventQueue.TryDequeue(out var generic))
                {
                    Interlocked.Decrement(ref queuedCriticalEvents);
                    Interlocked.Decrement(ref queuedEvents);
                    genericBatch.Add(generic);
                    continue;
                }
                if (criticalEntityQueue.TryDequeue(out var criticalEntity))
                {
                    Interlocked.Decrement(ref queuedCriticalEvents);
                    Interlocked.Decrement(ref queuedEvents);
                    entityBatch.Add(criticalEntity);
                    continue;
                }
                if (overflowEventQueue.TryDequeue(out var overflowGeneric))
                {
                    Interlocked.Decrement(ref queuedOverflowEvents);
                    genericBatch.Add(overflowGeneric);
                    continue;
                }
                if (overflowEntityQueue.TryDequeue(out var overflowEntity))
                {
                    Interlocked.Decrement(ref queuedOverflowEvents);
                    entityBatch.Add(overflowEntity);
                    continue;
                }
                if (bulkEntityQueue.TryDequeue(out var bulkEntity))
                {
                    Interlocked.Decrement(ref queuedBulkEntityEvents);
                    Interlocked.Decrement(ref queuedEvents);
                    entityBatch.Add(bulkEntity);
                    continue;
                }
                break;
            }

            var count = genericBatch.Count + entityBatch.Count;
            if (count == 0) break;
            try
            {
                writeGate.Wait();
                try { InsertMixedEventsPrepared(conn, cmd, genericBatch, entityBatch); }
                finally { writeGate.Release(); }
                Interlocked.Add(ref writtenEvents, count);
                Interlocked.Add(ref writtenEntityEvents, entityBatch.Count);
            }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                api.Logger.Error("[BlockLogger] Final event flush failed: {0}", ex);
                break;
            }
        }

        while (!entityAggregateQueue.IsEmpty)
        {
            aggregateBatch.Clear();
            while (aggregateBatch.Count < 256 && entityAggregateQueue.TryDequeue(out var aggregate))
            {
                Interlocked.Decrement(ref queuedEntityAggregates);
                aggregateBatch.Add(aggregate);
            }
            if (aggregateBatch.Count == 0) break;
            try
            {
                writeGate.Wait();
                try { InsertEntityAggregatesPrepared(conn, aggregateCmd, aggregateBatch); }
                finally { writeGate.Release(); }
                Interlocked.Add(ref writtenEntityAggregates, aggregateBatch.Count);
            }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                api.Logger.Error("[BlockLogger] Final entity aggregate flush failed: {0}", ex);
                break;
            }
        }
    }

    private void DrainSnapshotsPrepared(SqliteConnection conn, SqliteCommand cmd, List<ContainerSnapshotRecord> batch)
    {
        while (true)
        {
            batch.Clear();
            while (batch.Count < 500 && snapshotQueue.TryDequeue(out var r))
            {
                batch.Add(r);
                Interlocked.Decrement(ref queuedSnapshots);
            }
            if (batch.Count == 0) break;
            try
            {
                writeGate.Wait();
                try { InsertSnapshotsPrepared(conn, cmd, batch); }
                finally { writeGate.Release(); }
                Interlocked.Add(ref writtenSnapshots, batch.Count);
            }
            catch (Exception ex) { MarkStorageError(ex); api.Logger.Error("[BlockLogger] Final snapshot flush failed: {0}", ex); break; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
        cts.Cancel();
        StopEventQueries();
        StopSnapshotQueries();

        try { eventSignal.Release(); } catch { }

        bool workersStopped = false;
        try { workersStopped = Task.WaitAll(new[] { eventWriterTask, queryTask, snapshotQueryTask, migrationTask }, 12_000); }
        catch { workersStopped = eventWriterTask.IsCompleted && queryTask.IsCompleted && snapshotQueryTask.IsCompleted && migrationTask.IsCompleted; }

        if (eventWriterTask.IsCompleted && Volatile.Read(ref backupInProgress) == 0)
        {
            bool gateHeld = false;
            try
            {
                gateHeld = writeGate.Wait(TimeSpan.FromSeconds(2));
                if (gateHeld)
                {
                    var sw = Stopwatch.StartNew();
                    WithConnection(conn =>
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);";
                        cmd.ExecuteNonQuery();
                        return 0;
                    });
                    sw.Stop();
                    SuiteTimingRegistry.Record("BlockLogger final SQLite maintenance", sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                MarkStorageError(ex);
                api.Logger.Warning("[BlockLogger/Storage] Final SQLite maintenance failed: {0}: {1}", ex.GetType().Name, ex.Message);
            }
            finally { if (gateHeld) writeGate.Release(); }
        }

        if (!workersStopped)
        {
            // Do not dispose synchronization primitives underneath a still-draining worker.
            // The cancellation token is already set; the worker will exit once its final flush
            // finishes, and the process/server shutdown can reclaim these small objects safely.
            api.Logger.Warning("[BlockLogger/Storage] Shutdown drain exceeded 12 seconds. Workers remain cancelled; synchronization objects were left intact to avoid a dispose race.");
            return;
        }

        eventSignal.Dispose();
        queryQueue.Dispose();
        snapshotQueryQueue.Dispose();
        writeGate.Dispose();
        cts.Dispose();
    }
}

public sealed class BlockLoggerPurgeResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int KeepDays { get; set; }
    public int EventsRemoved { get; set; }
    public int SnapshotsRemoved { get; set; }
    public int EntityActivityRemoved { get; set; }
    public bool Complete { get; set; }
    public bool MoreRowsMayRemain { get; set; }
    public bool CompactRequested { get; set; }
    public bool Compacted { get; set; }
    public string CompactMessage { get; set; } = "";
    public long DatabaseBytesBefore { get; set; }
    public long DatabaseBytesAfter { get; set; }
    public long WalBytesBefore { get; set; }
    public long WalBytesAfter { get; set; }
    public long DurationMilliseconds { get; set; }
    public int TotalRemoved => EventsRemoved + SnapshotsRemoved + EntityActivityRemoved;
}

