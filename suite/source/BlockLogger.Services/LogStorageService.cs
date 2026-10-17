using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Models;
using BlockLogger.WebDashboard;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Server;

namespace BlockLogger.Services;

public sealed class LogStorageService : IDisposable
{
	private sealed record EventRequest(int X, int Y, int Z, int Dimension, int Radius, int MaxCount, int Offset, string? Text, string? Tag, bool EntityOnly, bool PlayerOnly, string? Player, long EnqueuedUnixMilliseconds, TaskCompletionSource<List<LogEventRecord>> Completion)
	{
		public bool ExactUid { get; init; }
	}

	private sealed record SnapshotRequest(int X, int Y, int Z, int Dimension, int Radius, int MaxCount, string? Text, long EnqueuedUnixMilliseconds, TaskCompletionSource<List<ContainerSnapshotRecord>> Completion);

	private sealed record StorageBatchMetric(long UnixMilliseconds, int Rows, long Milliseconds);

	private sealed record StorageQueryMetric(long UnixMilliseconds, string Kind, long Milliseconds);

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

	private readonly RecoveryQueryRunner recoveryQueries = new RecoveryQueryRunner();

	private volatile int autoBackupIntervalHours;

	private volatile int backupRetentionCount;

	private const int CorruptBackupRetentionCount = 2;

	private readonly ConcurrentQueue<QueuedEventPayload> criticalEventQueue = new ConcurrentQueue<QueuedEventPayload>();

	private readonly ConcurrentQueue<QueuedEntityEventPayload> criticalEntityQueue = new ConcurrentQueue<QueuedEntityEventPayload>();

	private readonly ConcurrentQueue<QueuedEntityEventPayload> bulkEntityQueue = new ConcurrentQueue<QueuedEntityEventPayload>();

	private readonly ConcurrentQueue<QueuedEventPayload> overflowEventQueue = new ConcurrentQueue<QueuedEventPayload>();

	private readonly ConcurrentQueue<QueuedEntityEventPayload> overflowEntityQueue = new ConcurrentQueue<QueuedEntityEventPayload>();

	private readonly ConcurrentQueue<EntityActivityAggregate> entityAggregateQueue = new ConcurrentQueue<EntityActivityAggregate>();

	private readonly ConcurrentQueue<ContainerSnapshotRecord> snapshotQueue = new ConcurrentQueue<ContainerSnapshotRecord>();

	private readonly SemaphoreSlim eventSignal = new SemaphoreSlim(0);

	private readonly BlockingCollection<EventRequest> queryQueue = new BlockingCollection<EventRequest>(new ConcurrentQueue<EventRequest>(), 64);

	private readonly BlockingCollection<SnapshotRequest> snapshotQueryQueue = new BlockingCollection<SnapshotRequest>(new ConcurrentQueue<SnapshotRequest>(), 32);

	private readonly CancellationTokenSource cts = new CancellationTokenSource();

	private int disposeState;

	private readonly SemaphoreSlim writeGate = new SemaphoreSlim(1, 1);

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

	private readonly ConcurrentQueue<StorageBatchMetric> recentBatchMetrics = new ConcurrentQueue<StorageBatchMetric>();

	private readonly ConcurrentQueue<StorageQueryMetric> recentQueryMetrics = new ConcurrentQueue<StorageQueryMetric>();

	private int recentBatchMetricCount;

	private int recentQueryMetricCount;

	public bool IsReady => migrationComplete;

	public long PendingEvents => Interlocked.Read(in queuedEvents) + Interlocked.Read(in queuedOverflowEvents);

	public long PendingSnapshots => Interlocked.Read(in queuedSnapshots);

	public long PendingCriticalEvents => Interlocked.Read(in queuedCriticalEvents);

	public long PendingBulkEntityEvents => Interlocked.Read(in queuedBulkEntityEvents);

	public long PendingOverflowEvents => Interlocked.Read(in queuedOverflowEvents);

	public long PeakPendingOverflowEvents => Interlocked.Read(in peakQueuedOverflowEvents);

	public long OverflowAcceptedEvents => Interlocked.Read(in overflowAcceptedEvents);

	public long OverflowRejectedEvents => Interlocked.Read(in overflowRejectedEvents);

	public int EmergencyOverflowCapacity => emergencyOverflowCapacity;

	public long DroppedEvents => Interlocked.Read(in droppedEvents);

	public long DroppedSnapshots => Interlocked.Read(in droppedSnapshots);

	public long WrittenEvents => Interlocked.Read(in writtenEvents);

	public long WrittenEntityEvents => Interlocked.Read(in writtenEntityEvents);

	public long WrittenEntityAggregates => Interlocked.Read(in writtenEntityAggregates);

	public long PendingEntityAggregates => Interlocked.Read(in queuedEntityAggregates);

	public long PeakPendingEntityAggregates => Interlocked.Read(in peakQueuedEntityAggregates);

	public long DroppedEntityAggregates => Interlocked.Read(in droppedEntityAggregates);

	public int QueueCapacity => maxQueue;

	public long DroppedEntityEvents => Interlocked.Read(in droppedEntityEvents);

	public long WrittenSnapshots => Interlocked.Read(in writtenSnapshots);

	public long PeakPendingEvents => Interlocked.Read(in peakPendingEvents);

	public long LastBatchRows => Interlocked.Read(in lastBatchRows);

	public long LastBatchEntityRows => Interlocked.Read(in lastBatchEntityRows);

	public long LastBatchElapsedMs => Interlocked.Read(in lastBatchElapsedMs);

	public long LastSuccessfulWriteUnixMilliseconds => Interlocked.Read(in lastSuccessfulWriteUnixMilliseconds);

	public long LastStorageErrorUnixMilliseconds => Interlocked.Read(in lastStorageErrorUnixMilliseconds);

	public string LastStorageError => Volatile.Read(in lastStorageError) ?? "";

	public string DatabasePath => dbPath;

	public string LegacyEventLogPath => eventLogDir;

	public string LegacySnapshotLogPath => snapshotLogDir;

	public LogStorageService(ICoreServerAPI api, int maxQueue, int emergencyOverflowCapacity, int flushIntervalMs = 750, bool autoMigrateLegacyLogs = false, int autoBackupIntervalHours = 72, int backupRetentionCount = 2)
	{
		this.api = api;
		this.maxQueue = Math.Max(100, maxQueue);
		this.emergencyOverflowCapacity = Math.Max(1000, emergencyOverflowCapacity);
		this.flushIntervalMs = Math.Clamp(flushIntervalMs, 100, 5000);
		this.autoBackupIntervalHours = Math.Clamp(autoBackupIntervalHours, 0, 8760);
		this.backupRetentionCount = Math.Clamp(backupRetentionCount, 1, 20);
		currentWorldId = api.World.SavegameIdentifier ?? "unknownworld";
		string fullPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "BlockLogger"));
		eventLogDir = Path.Combine(fullPath, "logs");
		snapshotLogDir = Path.Combine(fullPath, "containerlogs");
		string worldRoot = SuiteDataPaths.GetWorldRoot(api);
		backupsPath = Path.Combine(worldRoot, "backups");
		Directory.CreateDirectory(backupsPath);
		dbPath = Path.Combine(worldRoot, "blocklogger.db");
		RecoverBlockLoggerDatabaseIfNeeded();
		MigrateLegacyDatabaseIfNeeded(Path.Combine(fullPath, "blocklogger.db"), dbPath);
		InitializeDatabase();
		TrimDatabaseBackups();
		MaybeScheduleDatabaseBackup(force: false);
		migrationTask = (autoMigrateLegacyLogs ? StartDedicatedWorker(() =>
		{
			MigrateLegacyJsonlSafe(cts.Token);
		}, cts.Token) : Task.CompletedTask);
		if (!autoMigrateLegacyLogs)
		{
			migrationComplete = true;
		}
		eventWriterTask = StartDedicatedWorker(() =>
		{
			EventWriterLoop(cts.Token);
		}, cts.Token);
		queryTask = StartDedicatedWorker(() =>
		{
			QueryLoop(cts.Token);
		}, CancellationToken.None);
		snapshotQueryTask = StartDedicatedWorker(() =>
		{
			SnapshotQueryLoop(cts.Token);
		}, CancellationToken.None);
	}

	private static Task StartDedicatedWorker(Action action, CancellationToken token)
	{
		return Task.Factory.StartNew(() =>
		{
			try
			{
				Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
			}
			catch
			{
			}
			action();
		}, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	private void MarkStorageSuccess()
	{
		Interlocked.Exchange(ref lastSuccessfulWriteUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
	}

	private void MarkStorageError(Exception ex)
	{
		if (ex is SqliteException ex2 && (ex2.SqliteErrorCode == 5 || ex2.SqliteErrorCode == 6))
		{
			Interlocked.Increment(ref busyOrLockedErrors);
		}
		Volatile.Write(ref lastStorageError, ex.GetType().Name + ": " + ex.Message);
		Interlocked.Exchange(ref lastStorageErrorUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
	}

	private void RecordQueryMetric(string kind, long milliseconds)
	{
		Interlocked.Increment(ref queryCount);
		Interlocked.Add(ref queryTotalMs, milliseconds);
		Interlocked.Exchange(ref lastQueryMs, milliseconds);
		Volatile.Write(ref lastQueryKind, kind);
		if (milliseconds >= 100)
		{
			Interlocked.Increment(ref slowQueryCount);
		}
		long num;
		do
		{
			num = Interlocked.Read(in queryMaxMs);
		}
		while (milliseconds > num && Interlocked.CompareExchange(ref queryMaxMs, milliseconds, num) != num);
		long num2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		recentQueryMetrics.Enqueue(new StorageQueryMetric(num2, kind, milliseconds));
		Interlocked.Increment(ref recentQueryMetricCount);
		StorageQueryMetric result;
		StorageQueryMetric result2;
		while ((Volatile.Read(in recentQueryMetricCount) > 512 || (recentQueryMetrics.TryPeek(out result) && result.UnixMilliseconds < num2 - 300000)) && recentQueryMetrics.TryDequeue(out result2))
		{
			Interlocked.Decrement(ref recentQueryMetricCount);
		}
	}

	public DatabaseHealthSnapshot GetDatabaseHealth(int retentionDays)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		long recentCutoff = num - 300000;
		long minuteCutoff = num - 60000;
		List<StorageBatchMetric> source = (from x in recentBatchMetrics.ToArray()
			where x.UnixMilliseconds >= recentCutoff
			select x).ToList();
		List<double> list = (from x in ((IEnumerable<StorageBatchMetric>)source).Select((Func<StorageBatchMetric, double>)((StorageBatchMetric x) => x.Milliseconds))
			orderby x
			select x).ToList();
		double p95BatchMilliseconds = ((list.Count == 0) ? 0.0 : list[Math.Clamp((int)Math.Ceiling((double)list.Count * 0.95) - 1, 0, list.Count - 1)]);
		long num2 = Interlocked.Read(in writerBatchCount);
		List<double> list2 = (from x in (from x in recentQueryMetrics.ToArray()
				where x.UnixMilliseconds >= recentCutoff
				select x).Select((Func<StorageQueryMetric, double>)((StorageQueryMetric x) => x.Milliseconds))
			orderby x
			select x).ToList();
		double p95QueryMilliseconds = ((list2.Count == 0) ? 0.0 : list2[Math.Clamp((int)Math.Ceiling((double)list2.Count * 0.95) - 1, 0, list2.Count - 1)]);
		long num3 = Interlocked.Read(in queryCount);
		return new DatabaseHealthSnapshot
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
			BatchCount = num2,
			AverageBatchMilliseconds = ((num2 <= 0) ? 0.0 : ((double)Interlocked.Read(in writerBatchTotalMs) / (double)num2)),
			P95BatchMilliseconds = p95BatchMilliseconds,
			MaxBatchMilliseconds = Interlocked.Read(in writerBatchMaxMs),
			RowsPerMinute = source.Where((StorageBatchMetric x) => x.UnixMilliseconds >= minuteCutoff).Sum((StorageBatchMetric x) => x.Rows),
			QueryQueueDepth = queryQueue.Count,
			SnapshotQueryQueueDepth = snapshotQueryQueue.Count,
			QueryCount = num3,
			AverageQueryMilliseconds = ((num3 <= 0) ? 0.0 : ((double)Interlocked.Read(in queryTotalMs) / (double)num3)),
			P95QueryMilliseconds = p95QueryMilliseconds,
			MaxQueryMilliseconds = Interlocked.Read(in queryMaxMs),
			LastQueryMilliseconds = Interlocked.Read(in lastQueryMs),
			LastQueryKind = (Volatile.Read(in lastQueryKind) ?? ""),
			SlowQueryCount = Interlocked.Read(in slowQueryCount),
			WriteGateBusy = (writeGate.CurrentCount == 0),
			BusyOrLockedErrors = Interlocked.Read(in busyOrLockedErrors),
			LastIntegrityCheckUnixMilliseconds = Interlocked.Read(in lastIntegrityCheckUnixMilliseconds),
			LastIntegrityCheckMilliseconds = Interlocked.Read(in lastIntegrityCheckMilliseconds),
			LastBackupUnixMilliseconds = Interlocked.Read(in lastBackupUnixMilliseconds),
			BackupCount = (Directory.Exists(backupsPath) ? Directory.EnumerateFiles(backupsPath, "blocklogger-*.db").Count((string x) => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)) : 0),
			BackupBytes = GetBackupBytes(includeCorrupt: false),
			BackupRetentionCount = backupRetentionCount,
			AutoBackupIntervalHours = autoBackupIntervalHours,
			BackupInProgress = (Volatile.Read(in backupInProgress) != 0),
			LastSuccessfulWriteUnixMilliseconds = LastSuccessfulWriteUnixMilliseconds,
			LastError = LastStorageError,
			LastErrorUnixMilliseconds = LastStorageErrorUnixMilliseconds,
			RetentionDays = retentionDays,
			Integrity = (Volatile.Read(in lastIntegrityResult) ?? "Not run"),
			Message = (IsReady ? "SQLite writer/query services are initialized." : "SQLite is still completing legacy migration/index work.")
		};
		static long FileSize(string path)
		{
			try
			{
				return File.Exists(path) ? new FileInfo(path).Length : 0;
			}
			catch
			{
				return 0L;
			}
		}
	}

	public Task<DatabaseHealthSnapshot> CheckDatabaseIntegrityAsync(int retentionDays)
	{
		return Task.Run(() =>
		{
			DatabaseHealthSnapshot databaseHealth = GetDatabaseHealth(retentionDays);
			try
			{
				Stopwatch stopwatch = Stopwatch.StartNew();
				using SqliteConnection sqliteConnection = OpenConnection();
				using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
				sqliteCommand.CommandText = "PRAGMA quick_check;";
				string text = Convert.ToString(sqliteCommand.ExecuteScalar()) ?? "unknown";
				stopwatch.Stop();
				Interlocked.Exchange(ref lastIntegrityCheckMilliseconds, stopwatch.ElapsedMilliseconds);
				Interlocked.Exchange(ref lastIntegrityCheckUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
				databaseHealth.LastIntegrityCheckMilliseconds = stopwatch.ElapsedMilliseconds;
				databaseHealth.LastIntegrityCheckUnixMilliseconds = Interlocked.Read(in lastIntegrityCheckUnixMilliseconds);
				Volatile.Write(ref lastIntegrityResult, text);
				databaseHealth.Integrity = text;
				databaseHealth.Message = (string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase) ? "SQLite quick_check returned OK." : ("SQLite quick_check returned: " + text));
			}
			catch (Exception ex)
			{
				MarkStorageError(ex);
				Volatile.Write(ref lastIntegrityResult, "Failed");
				databaseHealth.Integrity = "Failed";
				databaseHealth.Message = ex.GetType().Name + ": " + ex.Message;
			}
			return databaseHealth;
		});
	}

	public void UpdateRuntimeSettings(int newMaxQueue, int newEmergencyOverflowCapacity, int newFlushIntervalMs, int newAutoBackupIntervalHours, int newBackupRetentionCount)
	{
		maxQueue = Math.Max(100, newMaxQueue);
		emergencyOverflowCapacity = Math.Max(1000, newEmergencyOverflowCapacity);
		flushIntervalMs = Math.Clamp(newFlushIntervalMs, 100, 5000);
		autoBackupIntervalHours = Math.Clamp(newAutoBackupIntervalHours, 0, 8760);
		backupRetentionCount = Math.Clamp(newBackupRetentionCount, 1, 20);
		TrimDatabaseBackups();
	}

	public bool TryEnqueue(LogEventRecord record)
	{
		string[] array = new string[16];
		int num = 0;
		foreach (KeyValuePair<string, string> metum in record.Meta)
		{
			if (num >= 16)
			{
				break;
			}
			array[num++] = metum.Key;
			array[num++] = metum.Value;
		}
		return TryEnqueueFast(new QueuedEventPayload(record.TimestampUtc, record.IngameDate, record.Action, record.PlayerUid, record.PlayerName, record.WorldId, record.Dimension, record.X, record.Y, record.Z, record.BlockCode, array[0], array[1], array[2], array[3], array[4], array[5], array[6], array[7], array[8], array[9], array[10], array[11], array[12], array[13], array[14], array[15]));
	}

	public bool TryEnqueueFast(in QueuedEventPayload record)
	{
		Interlocked.Increment(ref queuedCriticalEvents);
		long num = Interlocked.Increment(ref queuedEvents);
		ObservePeak(ref peakPendingEvents, num + Interlocked.Read(in queuedOverflowEvents));
		if (num > maxQueue)
		{
			Interlocked.Decrement(ref queuedEvents);
			Interlocked.Decrement(ref queuedCriticalEvents);
			return TryEnqueueOverflow(in record);
		}
		criticalEventQueue.Enqueue(record);
		if (num == 1 && Interlocked.Read(in queuedOverflowEvents) == 0)
		{
			eventSignal.Release();
		}
		return true;
	}

	public bool TryEnqueueEntityFast(in QueuedEntityEventPayload record)
	{
		bool isBulkEntity = record.IsBulkEntity;
		int num = maxQueue;
		if (isBulkEntity)
		{
			long num2 = Interlocked.Increment(ref queuedBulkEntityEvents);
			if (num2 > num)
			{
				Interlocked.Decrement(ref queuedBulkEntityEvents);
				return TryEnqueueOverflow(in record);
			}
		}
		else
		{
			Interlocked.Increment(ref queuedCriticalEvents);
		}
		long num3 = Interlocked.Increment(ref queuedEvents);
		ObservePeak(ref peakPendingEvents, num3 + Interlocked.Read(in queuedOverflowEvents));
		if (num3 > maxQueue)
		{
			Interlocked.Decrement(ref queuedEvents);
			if (isBulkEntity)
			{
				Interlocked.Decrement(ref queuedBulkEntityEvents);
			}
			else
			{
				Interlocked.Decrement(ref queuedCriticalEvents);
			}
			return TryEnqueueOverflow(in record);
		}
		if (isBulkEntity)
		{
			bulkEntityQueue.Enqueue(record);
		}
		else
		{
			criticalEntityQueue.Enqueue(record);
		}
		if (num3 == 1 && Interlocked.Read(in queuedOverflowEvents) == 0)
		{
			eventSignal.Release();
		}
		return true;
	}

	private bool TryReserveOverflowSlot(out long pending)
	{
		pending = Interlocked.Increment(ref queuedOverflowEvents);
		if (pending > emergencyOverflowCapacity)
		{
			Interlocked.Decrement(ref queuedOverflowEvents);
			Interlocked.Increment(ref overflowRejectedEvents);
			pending = 0L;
			return false;
		}
		Interlocked.Increment(ref overflowAcceptedEvents);
		ObservePeak(ref peakQueuedOverflowEvents, pending);
		ObservePeak(ref peakPendingEvents, Interlocked.Read(in queuedEvents) + pending);
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
		if (pending == 1)
		{
			eventSignal.Release();
		}
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
		if (pending == 1)
		{
			eventSignal.Release();
		}
		return true;
	}

	public bool TryEnqueueEntityAggregate(EntityActivityAggregate aggregate)
	{
		int num = Math.Max(128, maxQueue / 4);
		long num2 = Interlocked.Increment(ref queuedEntityAggregates);
		if (num2 > num)
		{
			Interlocked.Decrement(ref queuedEntityAggregates);
			Interlocked.Increment(ref droppedEntityAggregates);
			MaybeReportDrops();
			return false;
		}
		ObservePeak(ref peakQueuedEntityAggregates, num2);
		entityAggregateQueue.Enqueue(aggregate);
		if (num2 == 1)
		{
			eventSignal.Release();
		}
		return true;
	}

	public bool TryEnqueue(ContainerSnapshotRecord record)
	{
		long num = Interlocked.Increment(ref queuedSnapshots);
		if (num > maxQueue)
		{
			Interlocked.Decrement(ref queuedSnapshots);
			Interlocked.Increment(ref droppedSnapshots);
			MaybeReportDrops();
			return false;
		}
		snapshotQueue.Enqueue(record);
		if (num == 1)
		{
			eventSignal.Release();
		}
		return true;
	}

	private void MaybeReportDrops()
	{
		long ticks = DateTime.UtcNow.Ticks;
		long num = Interlocked.Read(in lastDropWarningTicks);
		if (ticks - num >= TimeSpan.FromSeconds(30L).Ticks && Interlocked.CompareExchange(ref lastDropWarningTicks, ticks, num) == num)
		{
			api.Logger.Warning("[BlockLogger] Storage queue overloaded. Pending={0} (normalCap={1}, overflow={2}/{3}, critical={4}, bulkEntity={5}, aggregates={6}), OverflowAccepted={7}, OverflowRejected={8}, DroppedEvents={9}, DroppedAggregates={10}, DroppedSnapshots={11}. Further warnings are rate-limited.", PendingEvents, maxQueue, PendingOverflowEvents, emergencyOverflowCapacity, PendingCriticalEvents, PendingBulkEntityEvents, PendingEntityAggregates, OverflowAcceptedEvents, OverflowRejectedEvents, DroppedEvents, DroppedEntityAggregates, DroppedSnapshots);
		}
	}

	public Task<List<LogEventRecord>> QueryEventsAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? text, string? tag, bool entityOnly, int offset = 0)
	{
		TaskCompletionSource<List<LogEventRecord>> taskCompletionSource = NewTcs<List<LogEventRecord>>();
		if (!TryAddQuery(queryQueue, new EventRequest(x, y, z, dimension, radius, maxCount, Math.Clamp(offset, 0, 2000), text, tag, entityOnly, PlayerOnly: false, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), taskCompletionSource)))
		{
			taskCompletionSource.TrySetException(new InvalidOperationException("BlockLogger query queue is busy or stopped."));
		}
		return taskCompletionSource.Task;
	}

	public Task<List<LogEventRecord>> QueryPlayerEventsAsync(string player, int maxCount, string? category, int offset = 0)
	{
		TaskCompletionSource<List<LogEventRecord>> taskCompletionSource = NewTcs<List<LogEventRecord>>();
		if (!TryAddQuery(queryQueue, new EventRequest(0, 0, 0, 0, 0, maxCount, Math.Clamp(offset, 0, 2000), null, category, EntityOnly: false, PlayerOnly: true, player, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), taskCompletionSource)))
		{
			taskCompletionSource.TrySetException(new InvalidOperationException("BlockLogger query queue is busy or stopped."));
		}
		return taskCompletionSource.Task;
	}

	public Task<List<LogEventRecord>> QueryPlayerUidEventsAsync(string uid, int maxCount = 100)
	{
		if (string.IsNullOrWhiteSpace(uid) || uid.Length > 128)
		{
			throw new ArgumentException("A player UID is required.", "uid");
		}
		TaskCompletionSource<List<LogEventRecord>> taskCompletionSource = NewTcs<List<LogEventRecord>>();
		if (!TryAddQuery(queryQueue, new EventRequest(0, 0, 0, 0, 0, Math.Clamp(maxCount, 1, 100), 0, null, null, EntityOnly: false, PlayerOnly: true, uid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), taskCompletionSource)
		{
			ExactUid = true
		}))
		{
			taskCompletionSource.TrySetException(new InvalidOperationException("BlockLogger query queue is busy or stopped."));
		}
		return taskCompletionSource.Task;
	}

	public Task<RollbackHistory> QueryRollbackRecoveryAsync(string target, DateTime fromUtc, DateTime toUtc, RecoveryCell? areaCenter = null, int areaRadius = 0, CancellationToken cancellationToken = default(CancellationToken))
	{
		return recoveryQueries.RunAsync(OpenReadOnlyConnection, (SqliteConnection conn, CancellationToken token) =>
		{
			bool flag = areaCenter != null;
			if (flag && (areaRadius < 1 || areaRadius > 4096))
			{
				throw new ArgumentOutOfRangeException("areaRadius");
			}
			if (fromUtc >= toUtc || toUtc > DateTime.UtcNow || toUtc - fromUtc > TimeSpan.FromDays(7))
			{
				throw new ArgumentException("Invalid recovery time window.");
			}
			if (!IsReady || PendingCriticalEvents != 0L || PendingOverflowEvents != 0L || PendingSnapshots != 0)
			{
				throw new InvalidOperationException("Logger is indexing or writing recovery records; retry when critical/overflow/snapshot queues are empty.");
			}
			token.ThrowIfCancellationRequested();
			using SqliteTransaction sqliteTransaction = conn.BeginTransaction(deferred: true);
			if (!writeGate.Wait(5000, token))
			{
				throw new InvalidOperationException("Logger writer is busy; retry recovery preview.");
			}
			try
			{
				if (PendingCriticalEvents != 0L || PendingOverflowEvents != 0L || PendingSnapshots != 0L || Volatile.Read(in recoveryWriterInFlight) != 0)
				{
					throw new InvalidOperationException("Recovery records are still pending/in-flight; retry after the logger commits them.");
				}
				using SqliteCommand sqliteCommand = conn.CreateCommand();
				sqliteCommand.Transaction = sqliteTransaction;
				sqliteCommand.CommandText = "SELECT id FROM events LIMIT 1;";
				sqliteCommand.ExecuteScalar();
			}
			finally
			{
				writeGate.Release();
			}
			string text = "";
			if (!flag)
			{
				using SqliteCommand sqliteCommand2 = conn.CreateCommand();
				sqliteCommand2.Transaction = sqliteTransaction;
				sqliteCommand2.CommandText = "SELECT DISTINCT player_uid FROM events\n                    WHERE world_id=$world AND timestamp_ticks BETWEEN $from AND $to\n                    AND player_uid <> '' AND (player_uid=$target COLLATE NOCASE OR player_name=$target COLLATE NOCASE)\n                    UNION SELECT DISTINCT json_extract(CASE WHEN json_valid(meta_json) THEN meta_json ELSE '{}' END,'$.sourcePlayerUid')\n                    FROM events WHERE world_id=$world AND action=9 AND timestamp_ticks BETWEEN $from AND $to\n                    AND (json_extract(CASE WHEN json_valid(meta_json) THEN meta_json ELSE '{}' END,'$.sourcePlayerUid')=$target COLLATE NOCASE\n                         OR json_extract(CASE WHEN json_valid(meta_json) THEN meta_json ELSE '{}' END,'$.sourcePlayerName')=$target COLLATE NOCASE);";
				sqliteCommand2.Parameters.AddWithValue("$world", currentWorldId);
				sqliteCommand2.Parameters.AddWithValue("$from", fromUtc.Ticks);
				sqliteCommand2.Parameters.AddWithValue("$to", toUtc.Ticks);
				sqliteCommand2.Parameters.AddWithValue("$target", target);
				HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
				using (SqliteDataReader sqliteDataReader = sqliteCommand2.ExecuteReader())
				{
					while (sqliteDataReader.Read())
					{
						token.ThrowIfCancellationRequested();
						if (!sqliteDataReader.IsDBNull(0) && !string.IsNullOrWhiteSpace(sqliteDataReader.GetString(0)))
						{
							hashSet.Add(sqliteDataReader.GetString(0));
						}
					}
				}
				if (hashSet.Count != 1)
				{
					throw new InvalidOperationException("Exact name/UID not found or ambiguous in the selected time window. Use the UID.");
				}
				text = hashSet.First();
			}
			RollbackHistory rollbackHistory = new RollbackHistory
			{
				AreaMode = flag,
				PlayerUid = text,
				WorldId = currentWorldId
			};
			token.ThrowIfCancellationRequested();
			using SqliteCommand sqliteCommand3 = conn.CreateCommand();
			sqliteCommand3.Transaction = sqliteTransaction;
			sqliteCommand3.CommandText = "WITH cells AS MATERIALIZED (\n                    SELECT DISTINCT dimension,x,y,z FROM events WHERE world_id=$world AND (($area=0 AND player_uid=$uid) OR ($area=1 AND (dimension=$dimension OR (dimension IN ('0','overworld') AND $dimension='0'))\n                    AND x BETWEEN $minX AND $maxX AND y BETWEEN $minY AND $maxY AND z BETWEEN $minZ AND $maxZ\n                    AND (1.0*(x-$cx)*(x-$cx)+1.0*(y-$cy)*(y-$cy)+1.0*(z-$cz)*(z-$cz))<=$r2))\n                    AND timestamp_ticks BETWEEN $from AND $to AND action IN (1,2,3,4,5,6)\n                ), baselines AS MATERIALIZED (\n                    SELECT c.*, (SELECT MAX(prior.timestamp_ticks) FROM snapshots prior\n                    WHERE prior.world_id=$world AND (prior.dimension=c.dimension OR (prior.dimension IN ('0','overworld') AND c.dimension IN ('0','overworld')))\n                    AND prior.x=c.x AND prior.y=c.y AND prior.z=c.z AND prior.timestamp_ticks < $from) AS before_ticks FROM cells c\n                ) SELECT e.timestamp_ticks,e.ingame_date,e.action,e.player_uid,e.player_name,e.world_id,\n                    e.dimension,e.x,e.y,e.z,e.block_code,e.meta_json,e.id FROM events e\n                    WHERE e.world_id=$world AND (\n                    (e.action IN (1,2,3,4,5,6) AND EXISTS(SELECT 1 FROM baselines c WHERE (c.dimension=e.dimension OR (c.dimension IN ('0','overworld') AND e.dimension IN ('0','overworld'))) AND c.x=e.x AND c.y=e.y AND c.z=e.z\n                    AND (e.timestamp_ticks >= $from OR e.timestamp_ticks > c.before_ticks)))\n                    OR (e.action=9 AND e.timestamp_ticks BETWEEN $from AND $to AND (($area=0 AND (e.player_uid=$uid OR\n                    json_extract(CASE WHEN json_valid(e.meta_json) THEN e.meta_json ELSE '{}' END,'$.sourcePlayerUid')=$uid)) OR ($area=1 AND (e.dimension=$dimension OR (e.dimension IN ('0','overworld') AND $dimension='0'))\n                    AND e.x BETWEEN $minX AND $maxX AND e.y BETWEEN $minY AND $maxY AND e.z BETWEEN $minZ AND $maxZ\n                    AND (1.0*(e.x-$cx)*(e.x-$cx)+1.0*(e.y-$cy)*(e.y-$cy)+1.0*(e.z-$cz)*(e.z-$cz))<=$r2))))\n                    ORDER BY e.timestamp_ticks DESC,e.id DESC LIMIT 200001;";
			sqliteCommand3.Parameters.AddWithValue("$world", currentWorldId);
			sqliteCommand3.Parameters.AddWithValue("$uid", text);
			AddAreaParameters(sqliteCommand3, areaCenter, areaRadius);
			sqliteCommand3.Parameters.AddWithValue("$from", fromUtc.Ticks);
			sqliteCommand3.Parameters.AddWithValue("$to", toUtc.Ticks);
			using (SqliteDataReader sqliteDataReader2 = sqliteCommand3.ExecuteReader())
			{
				while (sqliteDataReader2.Read())
				{
					token.ThrowIfCancellationRequested();
					LogEventRecord logEventRecord = ReadEvent(sqliteDataReader2);
					logEventRecord.StorageId = sqliteDataReader2.GetInt64(12);
					rollbackHistory.Events.Add(logEventRecord);
				}
			}
			if (rollbackHistory.Events.Count > 200000)
			{
				throw new InvalidOperationException("Affected-location history exceeds 200,000 rows. Choose a shorter time window.");
			}
			token.ThrowIfCancellationRequested();
			using SqliteCommand sqliteCommand4 = conn.CreateCommand();
			sqliteCommand4.Transaction = sqliteTransaction;
			sqliteCommand4.CommandText = "WITH cells AS MATERIALIZED (\n                    SELECT DISTINCT dimension,x,y,z FROM events WHERE world_id=$world AND (($area=0 AND player_uid=$uid) OR ($area=1 AND (dimension=$dimension OR (dimension IN ('0','overworld') AND $dimension='0'))\n                    AND x BETWEEN $minX AND $maxX AND y BETWEEN $minY AND $maxY AND z BETWEEN $minZ AND $maxZ\n                    AND (1.0*(x-$cx)*(x-$cx)+1.0*(y-$cy)*(y-$cy)+1.0*(z-$cz)*(z-$cz))<=$r2))\n                    AND timestamp_ticks BETWEEN $from AND $to AND action IN (1,2,3,4,5,6)\n                ), baselines AS MATERIALIZED (\n                    SELECT c.*, (SELECT old.id FROM snapshots old\n                    WHERE old.world_id=$world AND (old.dimension=c.dimension OR (old.dimension IN ('0','overworld') AND c.dimension IN ('0','overworld')))\n                    AND old.x=c.x AND old.y=c.y AND old.z=c.z AND old.timestamp_ticks < $from\n                    ORDER BY old.timestamp_ticks DESC,old.id DESC LIMIT 1) AS before_id FROM cells c\n                ) SELECT s.timestamp_ticks,s.ingame_date,s.world_id,s.dimension,s.x,s.y,s.z,s.block_code,\n                    s.player_uid,s.player_name,s.inventory_id,s.reason,s.changed_slot_id,\n                    s.slot_count,s.non_empty_slot_count,s.total_item_count,s.slots_json,s.id\n                    FROM snapshots s WHERE s.world_id=$world\n                    AND EXISTS(SELECT 1 FROM baselines c WHERE (c.dimension=s.dimension OR (c.dimension IN ('0','overworld') AND s.dimension IN ('0','overworld'))) AND c.x=s.x AND c.y=s.y AND c.z=s.z\n                    AND (s.timestamp_ticks >= $from OR s.id=c.before_id))\n                    ORDER BY s.timestamp_ticks DESC,s.id DESC LIMIT 50001;";
			sqliteCommand4.Parameters.AddWithValue("$world", currentWorldId);
			sqliteCommand4.Parameters.AddWithValue("$uid", text);
			AddAreaParameters(sqliteCommand4, areaCenter, areaRadius);
			sqliteCommand4.Parameters.AddWithValue("$from", fromUtc.Ticks);
			sqliteCommand4.Parameters.AddWithValue("$to", toUtc.Ticks);
			using (SqliteDataReader sqliteDataReader3 = sqliteCommand4.ExecuteReader())
			{
				while (sqliteDataReader3.Read())
				{
					token.ThrowIfCancellationRequested();
					if (sqliteDataReader3.IsDBNull(16) || JToken.Parse(sqliteDataReader3.GetString(16)).Type != JTokenType.Array)
					{
						throw new InvalidDataException("Historical inventory snapshot JSON is invalid; no recovery was planned.");
					}
					ContainerSnapshotRecord containerSnapshotRecord = ReadSnapshot(sqliteDataReader3);
					containerSnapshotRecord.StorageId = sqliteDataReader3.GetInt64(17);
					rollbackHistory.Snapshots.Add(containerSnapshotRecord);
				}
			}
			if (rollbackHistory.Snapshots.Count > 50000)
			{
				throw new InvalidOperationException("Recovery snapshot history exceeds 50,000 rows; shorten the time window.");
			}
			sqliteTransaction.Commit();
			return rollbackHistory;
		}, cts.Token, cancellationToken);
	}

	private static void AddAreaParameters(SqliteCommand command, RecoveryCell? center, int radius)
	{
		command.Parameters.AddWithValue("$area", (!(center == null)) ? 1 : 0);
		command.Parameters.AddWithValue("$dimension", (center == null) ? "" : RecoveryCell.Normalize(center.Dimension));
		long num = center?.X ?? 0;
		long num2 = center?.Y ?? 0;
		long num3 = center?.Z ?? 0;
		command.Parameters.AddWithValue("$cx", num);
		command.Parameters.AddWithValue("$cy", num2);
		command.Parameters.AddWithValue("$cz", num3);
		command.Parameters.AddWithValue("$minX", num - radius);
		command.Parameters.AddWithValue("$maxX", num + radius);
		command.Parameters.AddWithValue("$minY", num2 - radius);
		command.Parameters.AddWithValue("$maxY", num2 + radius);
		command.Parameters.AddWithValue("$minZ", num3 - radius);
		command.Parameters.AddWithValue("$maxZ", num3 + radius);
		command.Parameters.AddWithValue("$r2", (long)radius * (long)radius);
	}

	public Task<List<ContainerSnapshotRecord>> QuerySnapshotsAsync(int x, int y, int z, int dimension, int radius, int maxCount, string? text)
	{
		TaskCompletionSource<List<ContainerSnapshotRecord>> taskCompletionSource = NewTcs<List<ContainerSnapshotRecord>>();
		if (!TryAddQuery(snapshotQueryQueue, new SnapshotRequest(x, y, z, dimension, radius, maxCount, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), taskCompletionSource)))
		{
			taskCompletionSource.TrySetException(new InvalidOperationException("BlockLogger snapshot query queue is busy or stopped."));
		}
		return taskCompletionSource.Task;
	}

	public Task<List<PerformanceBlockActivityHotspot>> QueryRecentBlockActivityHotspotsAsync(int minutes, int maxCount)
	{
		minutes = Math.Clamp(minutes, 1, 1440);
		maxCount = Math.Clamp(maxCount, 1, 200);
		long cutoff = DateTime.UtcNow.AddMinutes(-minutes).Ticks;
		return Task.Run(() => WithReadOnlyConnection((SqliteConnection conn) =>
		{
			List<PerformanceBlockActivityHotspot> list = new List<PerformanceBlockActivityHotspot>();
			using SqliteCommand sqliteCommand = conn.CreateCommand();
			sqliteCommand.CommandText = "\nSELECT\n    dimension,\n    x,\n    y,\n    z,\n    COALESCE(block_code, ''),\n    COUNT(*) AS event_count,\n    COUNT(DISTINCT action) AS action_count,\n    MAX(timestamp_ticks) AS last_ticks\nFROM events\nWHERE world_id = $worldId\n  AND timestamp_ticks >= $cutoff\n  AND COALESCE(block_code, '') <> ''\nGROUP BY dimension, x, y, z, block_code\nORDER BY event_count DESC, last_ticks DESC\nLIMIT $limit;";
			sqliteCommand.Parameters.AddWithValue("$worldId", currentWorldId);
			sqliteCommand.Parameters.AddWithValue("$cutoff", cutoff);
			sqliteCommand.Parameters.AddWithValue("$limit", maxCount);
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			while (sqliteDataReader.Read())
			{
				list.Add(new PerformanceBlockActivityHotspot
				{
					Dimension = (sqliteDataReader.IsDBNull(0) ? "" : sqliteDataReader.GetString(0)),
					X = sqliteDataReader.GetInt32(1),
					Y = sqliteDataReader.GetInt32(2),
					Z = sqliteDataReader.GetInt32(3),
					BlockCode = (sqliteDataReader.IsDBNull(4) ? "" : sqliteDataReader.GetString(4)),
					EventCount = sqliteDataReader.GetInt32(5),
					DistinctActions = sqliteDataReader.GetInt32(6),
					LastTimestampTicks = sqliteDataReader.GetInt64(7)
				});
			}
			return list;
		}));
	}

	public Task<int> CountEventsAsync()
	{
		return Task.Run(() => WithReadOnlyConnection((SqliteConnection conn) =>
		{
			using SqliteCommand sqliteCommand = conn.CreateCommand();
			sqliteCommand.CommandText = "SELECT COUNT(*) FROM events;";
			return Convert.ToInt32(sqliteCommand.ExecuteScalar());
		}));
	}

	public Task<int> CountSnapshotsAsync()
	{
		return Task.Run(() => WithReadOnlyConnection((SqliteConnection conn) =>
		{
			using SqliteCommand sqliteCommand = conn.CreateCommand();
			sqliteCommand.CommandText = "SELECT COUNT(*) FROM snapshots;";
			return Convert.ToInt32(sqliteCommand.ExecuteScalar());
		}));
	}

	public Task<int> PruneAsync(int retentionDays)
	{
		if (retentionDays <= 0)
		{
			return Task.FromResult(0);
		}
		if (Interlocked.CompareExchange(ref retentionInProgress, 1, 0) != 0)
		{
			return Task.FromResult(0);
		}
		return Task.Run(() =>
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			int num = 0;
			try
			{
				long cutoff = DateTime.UtcNow.AddDays(-retentionDays).Ticks;
				for (int i = 0; i < 8; i++)
				{
					if (cts.IsCancellationRequested)
					{
						break;
					}
					if (i > 0 && (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0))
					{
						break;
					}
					writeGate.Wait(cts.Token);
					int num2;
					try
					{
						num2 = WithConnection((SqliteConnection conn) =>
						{
							using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
							int num3 = 0;
							using (SqliteCommand sqliteCommand = conn.CreateCommand())
							{
								sqliteCommand.Transaction = sqliteTransaction;
								sqliteCommand.CommandText = "DELETE FROM events WHERE id IN (\n                                    SELECT id FROM events WHERE timestamp_ticks < $cutoff ORDER BY id LIMIT 500\n                                );";
								sqliteCommand.Parameters.AddWithValue("$cutoff", cutoff);
								num3 += sqliteCommand.ExecuteNonQuery();
							}
							using (SqliteCommand sqliteCommand2 = conn.CreateCommand())
							{
								sqliteCommand2.Transaction = sqliteTransaction;
								sqliteCommand2.CommandText = "DELETE FROM snapshots WHERE id IN (\n                                    SELECT id FROM snapshots WHERE timestamp_ticks < $cutoff ORDER BY id LIMIT 100\n                                );";
								sqliteCommand2.Parameters.AddWithValue("$cutoff", cutoff);
								num3 += sqliteCommand2.ExecuteNonQuery();
							}
							using (SqliteCommand sqliteCommand3 = conn.CreateCommand())
							{
								sqliteCommand3.Transaction = sqliteTransaction;
								sqliteCommand3.CommandText = "DELETE FROM entity_activity WHERE id IN (\n                                    SELECT id FROM entity_activity WHERE window_end_ticks < $cutoff ORDER BY id LIMIT 500\n                                );";
								sqliteCommand3.Parameters.AddWithValue("$cutoff", cutoff);
								num3 += sqliteCommand3.ExecuteNonQuery();
							}
							sqliteTransaction.Commit();
							return num3;
						});
					}
					finally
					{
						writeGate.Release();
					}
					num += num2;
					if (num2 == 0 || PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)
					{
						break;
					}
					Thread.Sleep(5);
				}
				return num;
			}
			catch (OperationCanceledException)
			{
				return num;
			}
			catch (Exception ex2)
			{
				MarkStorageError(ex2);
				api.Logger.Warning("[BlockLogger/Storage] Retention maintenance failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
				return num;
			}
			finally
			{
				stopwatch.Stop();
				SuiteTimingRegistry.Record("BlockLogger retention", stopwatch.Elapsed.TotalMilliseconds);
				Interlocked.Exchange(ref retentionInProgress, 0);
			}
		});
	}

	public Task<BlockLoggerPurgeResult> PurgeDataAsync(int keepDays, bool purgeEvents, bool purgeSnapshots, bool purgeEntityActivity, bool compactAfter)
	{
		keepDays = Math.Clamp(keepDays, 0, 3650);
		if (!purgeEvents && !purgeSnapshots && !purgeEntityActivity)
		{
			return Task.FromResult(new BlockLoggerPurgeResult
			{
				Success = false,
				Message = "Select at least one BlockLogger data category to purge."
			});
		}
		if (Interlocked.CompareExchange(ref retentionInProgress, 1, 0) != 0)
		{
			return Task.FromResult(new BlockLoggerPurgeResult
			{
				Success = false,
				Message = "BlockLogger retention/purge maintenance is already running."
			});
		}
		return Task.Run(() =>
		{
			BlockLoggerPurgeResult result = new BlockLoggerPurgeResult
			{
				KeepDays = keepDays,
				DatabaseBytesBefore = SafeFileLength(dbPath),
				WalBytesBefore = SafeFileLength(dbPath + "-wal")
			};
			Stopwatch stopwatch = Stopwatch.StartNew();
			long cutoffTicks = ((keepDays <= 0) ? long.MaxValue : DateTime.UtcNow.AddDays(-keepDays).Ticks);
			int num = 0;
			try
			{
				while (!cts.IsCancellationRequested && num < 500000 && stopwatch.Elapsed.TotalSeconds < 45.0)
				{
					int num2 = 0;
					writeGate.Wait(cts.Token);
					try
					{
						num2 = WithConnection((SqliteConnection conn) =>
						{
							using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
							int num3 = 0;
							if (purgeEvents)
							{
								int num4 = DeletePurgeBatch(conn, sqliteTransaction, "events", "timestamp_ticks", cutoffTicks, keepDays <= 0, 5000);
								result.EventsRemoved += num4;
								num3 += num4;
							}
							if (purgeSnapshots)
							{
								int num5 = DeletePurgeBatch(conn, sqliteTransaction, "snapshots", "timestamp_ticks", cutoffTicks, keepDays <= 0, 1000);
								result.SnapshotsRemoved += num5;
								num3 += num5;
							}
							if (purgeEntityActivity)
							{
								int num6 = DeletePurgeBatch(conn, sqliteTransaction, "entity_activity", "window_end_ticks", cutoffTicks, keepDays <= 0, 5000);
								result.EntityActivityRemoved += num6;
								num3 += num6;
							}
							sqliteTransaction.Commit();
							return num3;
						});
					}
					finally
					{
						writeGate.Release();
					}
					num += num2;
					if (num2 == 0)
					{
						result.Complete = true;
						break;
					}
					if (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)
					{
						Thread.Sleep(25);
					}
				}
				if (!result.Complete)
				{
					result.MoreRowsMayRemain = true;
				}
				if (PendingEvents == 0L && PendingSnapshots == 0L && PendingEntityAggregates == 0)
				{
					writeGate.Wait(cts.Token);
					try
					{
						WithConnection((SqliteConnection conn) =>
						{
							using SqliteCommand sqliteCommand = conn.CreateCommand();
							sqliteCommand.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
							sqliteCommand.ExecuteNonQuery();
							return 0;
						});
					}
					catch
					{
					}
					finally
					{
						writeGate.Release();
					}
				}
				if (compactAfter && result.Complete)
				{
					result.CompactRequested = true;
					TryCompactDatabase(result);
				}
				result.DatabaseBytesAfter = SafeFileLength(dbPath);
				result.WalBytesAfter = SafeFileLength(dbPath + "-wal");
				result.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
				result.Success = true;
				result.Message = (result.MoreRowsMayRemain ? $"Removed {num:N0} rows in this bounded maintenance pass. More matching rows may remain; run Purge again to continue." : ($"Purge completed. Removed {num:N0} rows." + (result.Compacted ? " Database compaction completed." : "")));
				return result;
			}
			catch (OperationCanceledException)
			{
				result.Message = "Purge cancelled because BlockLogger storage is shutting down.";
				return result;
			}
			catch (Exception ex2)
			{
				MarkStorageError(ex2);
				result.Message = ex2.GetType().Name + ": " + ex2.Message;
				return result;
			}
			finally
			{
				stopwatch.Stop();
				SuiteTimingRegistry.Record("BlockLogger manual purge", stopwatch.Elapsed.TotalMilliseconds);
				Interlocked.Exchange(ref retentionInProgress, 0);
			}
		});
	}

	private static int DeletePurgeBatch(SqliteConnection conn, SqliteTransaction tx, string table, string timeColumn, long cutoffTicks, bool purgeAll, int limit)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = (purgeAll ? $"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} ORDER BY id LIMIT $limit);" : $"DELETE FROM {table} WHERE id IN (SELECT id FROM {table} WHERE {timeColumn} < $cutoff ORDER BY id LIMIT $limit);");
		sqliteCommand.Parameters.AddWithValue("$limit", limit);
		if (!purgeAll)
		{
			sqliteCommand.Parameters.AddWithValue("$cutoff", cutoffTicks);
		}
		return Math.Max(0, sqliteCommand.ExecuteNonQuery());
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
			long num = SafeFileLength(dbPath);
			try
			{
				string text = Path.GetPathRoot(Path.GetFullPath(dbPath)) ?? "";
				if (!string.IsNullOrEmpty(text))
				{
					DriveInfo driveInfo = new DriveInfo(text);
					long num2 = Math.Max(536870912L, (long)((double)num * 1.15));
					if (driveInfo.AvailableFreeSpace < num2)
					{
						result.CompactMessage = $"Compaction skipped: about {(double)num2 / 1048576.0:0} MiB free space is required, but only {(double)driveInfo.AvailableFreeSpace / 1048576.0:0} MiB is available.";
						return;
					}
				}
			}
			catch
			{
			}
			writeGate.Wait(cts.Token);
			try
			{
				using SqliteConnection sqliteConnection = OpenConnection();
				using (SqliteCommand sqliteCommand = sqliteConnection.CreateCommand())
				{
					sqliteCommand.CommandText = "VACUUM;";
					sqliteCommand.ExecuteNonQuery();
				}
				using (SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand())
				{
					sqliteCommand2.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
					sqliteCommand2.ExecuteNonQuery();
				}
				result.Compacted = true;
				result.CompactMessage = "VACUUM completed and the WAL was truncated.";
			}
			finally
			{
				writeGate.Release();
			}
		}
		catch (Exception ex)
		{
			result.CompactMessage = "Compaction failed/skipped: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static long SafeFileLength(string path)
	{
		try
		{
			return File.Exists(path) ? new FileInfo(path).Length : 0;
		}
		catch
		{
			return 0L;
		}
	}

	private void RecoverBlockLoggerDatabaseIfNeeded()
	{
		if (!File.Exists(dbPath))
		{
			return;
		}
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadWrite;Pooling=False");
			sqliteConnection.Open();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "PRAGMA quick_check;";
			string text = Convert.ToString(sqliteCommand.ExecuteScalar()) ?? "unknown";
			if (string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			throw new InvalidDataException("SQLite quick_check returned: " + text);
		}
		catch (Exception ex)
		{
			api.Logger.Error("[BlockLogger/Storage] blocklogger.db failed startup integrity check: {0}", ex.Message);
			string text2 = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
			try
			{
				File.Move(dbPath, Path.Combine(backupsPath, "blocklogger-corrupt-" + text2 + ".db"), overwrite: true);
				TryMoveDatabaseSidecar(dbPath + "-wal", Path.Combine(backupsPath, "blocklogger-corrupt-" + text2 + ".db-wal"));
				TryMoveDatabaseSidecar(dbPath + "-shm", Path.Combine(backupsPath, "blocklogger-corrupt-" + text2 + ".db-shm"));
				string text3 = (from x in Directory.EnumerateFiles(backupsPath, "blocklogger-*.db")
					where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
					select x).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
				if (text3 != null)
				{
					File.Copy(text3, dbPath, overwrite: true);
					api.Logger.Warning("[BlockLogger/Storage] Restored blocklogger.db from backup {0}", Path.GetFileName(text3));
				}
				else
				{
					api.Logger.Warning("[BlockLogger/Storage] No healthy backup was available. Corrupt database was preserved and a new database will be created.");
				}
			}
			catch (Exception ex2)
			{
				api.Logger.Error("[BlockLogger/Storage] Database recovery failed: {0}", ex2);
			}
		}
	}

	private static void TryMoveDatabaseSidecar(string source, string destination)
	{
		try
		{
			if (File.Exists(source))
			{
				File.Move(source, destination, overwrite: true);
			}
		}
		catch
		{
		}
	}

	private void MigrateLegacyDatabaseIfNeeded(string legacyDbPath, string newDbPath)
	{
		if (File.Exists(newDbPath) || !File.Exists(legacyDbPath))
		{
			return;
		}
		try
		{
			api.Logger.Notification("[BlockLogger/Storage] Migrating SQLite database into Vintage Story data folder: {0}", newDbPath);
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + legacyDbPath + ";Mode=ReadOnly;Pooling=False");
			sqliteConnection.Open();
			using SqliteConnection sqliteConnection2 = new SqliteConnection("Data Source=" + newDbPath + ";Mode=ReadWriteCreate;Pooling=False");
			sqliteConnection2.Open();
			sqliteConnection.BackupDatabase(sqliteConnection2);
			int num = 0;
			using (SqliteTransaction sqliteTransaction = sqliteConnection2.BeginTransaction())
			{
				string[] array = new string[3] { "events", "snapshots", "entity_activity" };
				foreach (string text in array)
				{
					try
					{
						using SqliteCommand sqliteCommand = sqliteConnection2.CreateCommand();
						sqliteCommand.Transaction = sqliteTransaction;
						sqliteCommand.CommandText = "UPDATE " + text + " SET world_id=$world WHERE world_id IS NULL OR world_id='';";
						sqliteCommand.Parameters.AddWithValue("$world", currentWorldId);
						sqliteCommand.ExecuteNonQuery();
						using SqliteCommand sqliteCommand2 = sqliteConnection2.CreateCommand();
						sqliteCommand2.Transaction = sqliteTransaction;
						sqliteCommand2.CommandText = "DELETE FROM " + text + " WHERE world_id<>$world;";
						sqliteCommand2.Parameters.AddWithValue("$world", currentWorldId);
						num += Math.Max(0, sqliteCommand2.ExecuteNonQuery());
					}
					catch (SqliteException)
					{
					}
				}
				sqliteTransaction.Commit();
			}
			api.Logger.Notification("[BlockLogger/Storage] Legacy blocklogger.db migration completed for world {0}. Removed {1:N0} row(s) belonging to other worlds from the new per-world copy. Original database was left untouched as a rollback copy.", currentWorldId, num);
		}
		catch (Exception ex2)
		{
			api.Logger.Error("[BlockLogger/Storage] Legacy database migration failed; the new database will not replace the original: {0}", ex2);
			try
			{
				if (File.Exists(newDbPath))
				{
					File.Delete(newDbPath);
				}
			}
			catch
			{
			}
		}
	}

	public Task<string> CreateDatabaseBackupAsync(string reason = "manual")
	{
		return Task.Run(() => CreateDatabaseBackup(reason));
	}

	private string CreateDatabaseBackup(string reason)
	{
		if (cts.IsCancellationRequested)
		{
			return "";
		}
		if (Interlocked.CompareExchange(ref backupInProgress, 1, 0) != 0)
		{
			return "";
		}
		try
		{
			string value = new string((reason ?? "backup").Where((char ch) =>
			{
				bool flag2 = char.IsLetterOrDigit(ch);
				bool flag3 = flag2;
				if (!flag3)
				{
					bool flag4 = ((ch == '-' || ch == '_') ? true : false);
					flag3 = flag4;
				}
				return flag3;
			}).Take(24).ToArray());
			if (string.IsNullOrWhiteSpace(value))
			{
				value = "backup";
			}
			string text = Path.Combine(backupsPath, $"blocklogger-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{value}.db");
			bool flag = false;
			try
			{
				flag = writeGate.Wait(0);
				if (flag && PendingEvents == 0L && PendingSnapshots == 0L && PendingEntityAggregates == 0)
				{
					Stopwatch stopwatch = Stopwatch.StartNew();
					using SqliteConnection sqliteConnection = OpenConnection();
					using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
					sqliteCommand.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
					sqliteCommand.ExecuteNonQuery();
					stopwatch.Stop();
					SuiteTimingRegistry.Record("BlockLogger SQLite maintenance", stopwatch.Elapsed.TotalMilliseconds);
				}
			}
			catch
			{
			}
			finally
			{
				if (flag)
				{
					writeGate.Release();
				}
			}
			Stopwatch stopwatch2 = Stopwatch.StartNew();
			using SqliteConnection sqliteConnection2 = OpenReadOnlyConnection();
			using SqliteConnection sqliteConnection3 = new SqliteConnection("Data Source=" + text + ";Mode=ReadWriteCreate;Pooling=False");
			sqliteConnection3.Open();
			sqliteConnection2.BackupDatabase(sqliteConnection3);
			stopwatch2.Stop();
			SuiteTimingRegistry.Record("BlockLogger database backup", stopwatch2.Elapsed.TotalMilliseconds);
			Interlocked.Exchange(ref lastBackupUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			TrimDatabaseBackups();
			return text;
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
		if (!Directory.Exists(backupsPath))
		{
			return 0L;
		}
		long num = 0L;
		try
		{
			foreach (string item in Directory.EnumerateFiles(backupsPath, "blocklogger-*.db"))
			{
				bool flag = Path.GetFileName(item).Contains("corrupt", StringComparison.OrdinalIgnoreCase);
				if (!(!includeCorrupt & flag))
				{
					try
					{
						num += new FileInfo(item).Length;
					}
					catch
					{
					}
				}
			}
		}
		catch
		{
		}
		return num;
	}

	private void TrimDatabaseBackups()
	{
		if (!Directory.Exists(backupsPath))
		{
			return;
		}
		try
		{
			List<string> source = (from x in Directory.EnumerateFiles(backupsPath, "blocklogger-*.db")
				where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
				select x).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
			foreach (string item in source.Skip(Math.Clamp(backupRetentionCount, 1, 20)))
			{
				try
				{
					File.Delete(item);
				}
				catch
				{
				}
			}
			List<string> source2 = Directory.EnumerateFiles(backupsPath, "blocklogger-corrupt-*.db").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
			foreach (string item2 in source2.Skip(2))
			{
				try
				{
					File.Delete(item2);
				}
				catch
				{
				}
				try
				{
					File.Delete(item2 + "-wal");
				}
				catch
				{
				}
				try
				{
					File.Delete(item2 + "-shm");
				}
				catch
				{
				}
			}
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
			long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (!force && num - Interlocked.Read(in lastAutoBackupCheckUnixMilliseconds) < 3600000)
			{
				return;
			}
			Interlocked.Exchange(ref lastAutoBackupCheckUnixMilliseconds, num);
			CheckDatabaseGrowth(num);
			if ((!force && autoBackupIntervalHours <= 0) || Volatile.Read(in backupInProgress) != 0 || cts.IsCancellationRequested || (!force && (PendingEvents > 0 || PendingSnapshots > 0 || PendingEntityAggregates > 0)))
			{
				return;
			}
			long num2 = Interlocked.Read(in lastSuccessfulWriteUnixMilliseconds);
			if (!force && num2 > 0 && num - num2 < 600000)
			{
				return;
			}
			string text = (from x in Directory.EnumerateFiles(backupsPath, "blocklogger-*.db")
				where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
				select x).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			if (!force && text != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(text) < TimeSpan.FromHours(Math.Max(1, autoBackupIntervalHours)))
			{
				Interlocked.Exchange(ref lastBackupUnixMilliseconds, new DateTimeOffset(File.GetLastWriteTimeUtc(text), TimeSpan.Zero).ToUnixTimeMilliseconds());
				return;
			}
			Task.Factory.StartNew(() =>
			{
				try
				{
					Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
				}
				catch
				{
				}
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
		long num = Interlocked.Read(in lastDatabaseGrowthCheckUnixMilliseconds);
		if (num != 0L && nowUnixMilliseconds - num < 3600000)
		{
			return;
		}
		long num2 = 0L;
		try
		{
			if (File.Exists(dbPath))
			{
				num2 += new FileInfo(dbPath).Length;
			}
			string text = dbPath + "-wal";
			if (File.Exists(text))
			{
				num2 += new FileInfo(text).Length;
			}
		}
		catch
		{
			return;
		}
		long num3 = Interlocked.Exchange(ref lastDatabaseObservedBytes, num2);
		Interlocked.Exchange(ref lastDatabaseGrowthCheckUnixMilliseconds, nowUnixMilliseconds);
		if (num3 <= 0 || num2 <= num3)
		{
			return;
		}
		long num4 = num2 - num3;
		bool flag = num4 >= num3 / 4;
		if (num4 >= 536870912 && flag)
		{
			long num5 = Interlocked.Read(in lastDatabaseGrowthWarningUnixMilliseconds);
			if (num5 == 0L || nowUnixMilliseconds - num5 >= 21600000)
			{
				Interlocked.Exchange(ref lastDatabaseGrowthWarningUnixMilliseconds, nowUnixMilliseconds);
				api.Logger.Warning("[BlockLogger/Storage] Database + WAL grew unusually quickly: {0:0.0} MiB since the previous hourly size sample (now {1:0.0} MiB). This is advisory only; no automatic VACUUM or deletion was performed.", (double)num4 / 1048576.0, (double)num2 / 1048576.0);
			}
		}
	}

	private void InitializeDatabase()
	{
		WithConnection((SqliteConnection conn) =>
		{
			using SqliteCommand sqliteCommand = conn.CreateCommand();
			sqliteCommand.CommandText = "\nCREATE TABLE IF NOT EXISTS meta (\n    key TEXT PRIMARY KEY,\n    value TEXT NOT NULL\n);\n\nCREATE TABLE IF NOT EXISTS events (\n    id INTEGER PRIMARY KEY AUTOINCREMENT,\n    timestamp_ticks INTEGER NOT NULL,\n    timestamp_utc TEXT NOT NULL,\n    ingame_date TEXT,\n    action INTEGER NOT NULL,\n    player_uid TEXT,\n    player_name TEXT,\n    world_id TEXT,\n    dimension TEXT,\n    x INTEGER NOT NULL,\n    y INTEGER NOT NULL,\n    z INTEGER NOT NULL,\n    block_code TEXT,\n    meta_json TEXT,\n    legacy_key TEXT\n);\n\nDROP INDEX IF EXISTS idx_events_pos_time;\nCREATE INDEX IF NOT EXISTS idx_events_world_dim_pos_time ON events(world_id, dimension, x, y, z, timestamp_ticks DESC);\nCREATE INDEX IF NOT EXISTS idx_events_time ON events(timestamp_ticks DESC);\nCREATE INDEX IF NOT EXISTS idx_events_world_time ON events(world_id, timestamp_ticks DESC);\n-- Player history only indexes player-attributed events. System/entity telemetry with an\n-- empty player UID does not pay the write cost.\nDROP INDEX IF EXISTS idx_events_player_time;\nCREATE INDEX IF NOT EXISTS idx_events_player_name_time\nON events(world_id, player_name COLLATE NOCASE, timestamp_ticks DESC)\nWHERE player_uid <> '';\nCREATE INDEX IF NOT EXISTS idx_events_player_uid_time\nON events(world_id, player_uid, timestamp_ticks DESC)\nWHERE player_uid <> '';\nDROP INDEX IF EXISTS idx_events_action_time;\nCREATE UNIQUE INDEX IF NOT EXISTS idx_events_legacy_key ON events(legacy_key);\n\nCREATE TABLE IF NOT EXISTS snapshots (\n    id INTEGER PRIMARY KEY AUTOINCREMENT,\n    timestamp_ticks INTEGER NOT NULL,\n    timestamp_utc TEXT NOT NULL,\n    ingame_date TEXT,\n    world_id TEXT,\n    dimension TEXT,\n    x INTEGER NOT NULL,\n    y INTEGER NOT NULL,\n    z INTEGER NOT NULL,\n    block_code TEXT,\n    player_uid TEXT,\n    player_name TEXT,\n    inventory_id TEXT,\n    reason TEXT,\n    changed_slot_id INTEGER,\n    slot_count INTEGER,\n    non_empty_slot_count INTEGER,\n    total_item_count INTEGER,\n    slots_json TEXT\n);\n\nDROP INDEX IF EXISTS idx_snapshots_pos_time;\nCREATE INDEX IF NOT EXISTS idx_snapshots_world_dim_pos_time ON snapshots(world_id, dimension, x, y, z, timestamp_ticks DESC);\nCREATE INDEX IF NOT EXISTS idx_snapshots_time ON snapshots(timestamp_ticks DESC);\nCREATE INDEX IF NOT EXISTS idx_snapshots_world_time ON snapshots(world_id, timestamp_ticks DESC);\nDROP INDEX IF EXISTS idx_snapshots_inventory_time;\n\nCREATE TABLE IF NOT EXISTS entity_activity (\n    id INTEGER PRIMARY KEY AUTOINCREMENT,\n    window_start_ticks INTEGER NOT NULL,\n    window_end_ticks INTEGER NOT NULL,\n    action INTEGER NOT NULL,\n    entity_type_id INTEGER NOT NULL,\n    entity_code TEXT NOT NULL,\n    entity_class TEXT,\n    event_count INTEGER NOT NULL,\n    world_id TEXT,\n    dimension TEXT,\n    sample_x INTEGER NOT NULL,\n    sample_y INTEGER NOT NULL,\n    sample_z INTEGER NOT NULL,\n    region_x INTEGER NOT NULL DEFAULT 0,\n    region_z INTEGER NOT NULL DEFAULT 0\n);\nCREATE INDEX IF NOT EXISTS idx_entity_activity_time ON entity_activity(window_end_ticks DESC);\nDROP INDEX IF EXISTS idx_entity_activity_code_time;\n";
			sqliteCommand.ExecuteNonQuery();
			EnsureColumn(conn, "entity_activity", "region_x", "INTEGER NOT NULL DEFAULT 0");
			EnsureColumn(conn, "entity_activity", "region_z", "INTEGER NOT NULL DEFAULT 0");
			using (SqliteCommand sqliteCommand2 = conn.CreateCommand())
			{
				sqliteCommand2.CommandText = "\nDROP INDEX IF EXISTS idx_entity_activity_region_time;\nCREATE INDEX IF NOT EXISTS idx_entity_activity_world_dim_pos_time\nON entity_activity(world_id, dimension, sample_x, sample_y, sample_z, window_end_ticks DESC);";
				sqliteCommand2.ExecuteNonQuery();
			}
			return 0;
		});
	}

	private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA table_info(" + table + ");";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			if (string.Equals(sqliteDataReader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		sqliteDataReader.Close();
		using SqliteCommand sqliteCommand2 = conn.CreateCommand();
		sqliteCommand2.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
		sqliteCommand2.ExecuteNonQuery();
	}

	private bool WriteMixedBatchWithRetry(SqliteConnection conn, SqliteCommand eventCmd, SqliteCommand aggregateCmd, SqliteCommand snapshotCmd, List<QueuedEventPayload> genericBatch, List<QueuedEntityEventPayload> entityBatch, List<EntityActivityAggregate> aggregateBatch, List<ContainerSnapshotRecord> snapshotBatch, CancellationToken token)
	{
		int num = 0;
		int num2 = 100;
		long num3 = 0L;
		while (!token.IsCancellationRequested)
		{
			try
			{
				writeGate.Wait(token);
				try
				{
					InsertMixedWriterBatchPrepared(conn, eventCmd, aggregateCmd, snapshotCmd, genericBatch, entityBatch, aggregateBatch, snapshotBatch);
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
			catch (Exception ex2)
			{
				MarkStorageError(ex2);
				num++;
				long num4 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
				if (num == 1 || num4 - num3 >= 30000)
				{
					num3 = num4;
					api.Logger.Error("[BlockLogger] SQLite writer batch failed; retaining one bounded batch locally and retrying with backoff. Attempt={0}, batchRows={1}, pending={2}. {3}: {4}", num, genericBatch.Count + entityBatch.Count + aggregateBatch.Count + snapshotBatch.Count, PendingEvents + PendingSnapshots + PendingEntityAggregates, ex2.GetType().Name, ex2.Message);
				}
				if (token.WaitHandle.WaitOne(num2))
				{
					return false;
				}
				num2 = Math.Min(5000, num2 * 2);
			}
		}
		return false;
	}

	private void EventWriterLoop(CancellationToken token)
	{
		using SqliteConnection conn = OpenConnection();
		using SqliteCommand sqliteCommand = CreatePreparedEventInsert(conn);
		using SqliteCommand aggregateCmd = CreatePreparedEntityAggregateInsert(conn);
		using SqliteCommand sqliteCommand2 = CreatePreparedSnapshotInsert(conn);
		List<QueuedEventPayload> list = new List<QueuedEventPayload>(2500);
		List<QueuedEntityEventPayload> list2 = new List<QueuedEntityEventPayload>(2500);
		List<EntityActivityAggregate> list3 = new List<EntityActivityAggregate>(256);
		List<ContainerSnapshotRecord> list4 = new List<ContainerSnapshotRecord>(128);
		bool flag = false;
		while (!token.IsCancellationRequested)
		{
			try
			{
				int num = ((PendingEvents <= 0 && entityAggregateQueue.IsEmpty && Interlocked.Read(in queuedSnapshots) <= 0) ? Math.Max(60000, flushIntervalMs) : flushIntervalMs);
				eventSignal.Wait(TimeSpan.FromMilliseconds(num), token);
				MaybeScheduleDatabaseBackup(force: false);
				if (PendingEvents < maxQueue / 2 && PendingEvents > 0)
				{
					token.WaitHandle.WaitOne(flushIntervalMs);
				}
				do
				{
					Volatile.Write(ref recoveryWriterInFlight, 1);
					list.Clear();
					list2.Clear();
					list3.Clear();
					list4.Clear();
					int num2 = ((PendingEvents > maxQueue / 2) ? 2500 : 1000);
					int num3 = Math.Max(1, num2 / 4);
					int i;
					for (i = 0; i < num3; i++)
					{
						if (!criticalEventQueue.TryDequeue(out var result))
						{
							break;
						}
						Interlocked.Decrement(ref queuedCriticalEvents);
						Interlocked.Decrement(ref queuedEvents);
						list.Add(result);
					}
					for (; i < num3; i++)
					{
						if (!criticalEntityQueue.TryDequeue(out var result2))
						{
							break;
						}
						Interlocked.Decrement(ref queuedCriticalEvents);
						Interlocked.Decrement(ref queuedEvents);
						list2.Add(result2);
					}
					QueuedEventPayload result3;
					while (list.Count + list2.Count < num2 && overflowEventQueue.TryDequeue(out result3))
					{
						Interlocked.Decrement(ref queuedOverflowEvents);
						list.Add(result3);
					}
					QueuedEntityEventPayload result4;
					while (list.Count + list2.Count < num2 && overflowEntityQueue.TryDequeue(out result4))
					{
						Interlocked.Decrement(ref queuedOverflowEvents);
						list2.Add(result4);
					}
					QueuedEntityEventPayload result5;
					while (list.Count + list2.Count < num2 && bulkEntityQueue.TryDequeue(out result5))
					{
						Interlocked.Decrement(ref queuedBulkEntityEvents);
						Interlocked.Decrement(ref queuedEvents);
						list2.Add(result5);
					}
					QueuedEventPayload result6;
					while (list.Count + list2.Count < num2 && criticalEventQueue.TryDequeue(out result6))
					{
						Interlocked.Decrement(ref queuedCriticalEvents);
						Interlocked.Decrement(ref queuedEvents);
						list.Add(result6);
					}
					QueuedEntityEventPayload result7;
					while (list.Count + list2.Count < num2 && criticalEntityQueue.TryDequeue(out result7))
					{
						Interlocked.Decrement(ref queuedCriticalEvents);
						Interlocked.Decrement(ref queuedEvents);
						list2.Add(result7);
					}
					EntityActivityAggregate result8;
					while (list3.Count < 256 && entityAggregateQueue.TryDequeue(out result8))
					{
						Interlocked.Decrement(ref queuedEntityAggregates);
						list3.Add(result8);
					}
					ContainerSnapshotRecord result9;
					while (list4.Count < 128 && snapshotQueue.TryDequeue(out result9))
					{
						Interlocked.Decrement(ref queuedSnapshots);
						list4.Add(result9);
					}
					int num4 = list.Count + list2.Count;
					int num5 = num4 + list3.Count + list4.Count;
					if (num5 > 0)
					{
						flag = true;
						Stopwatch stopwatch = Stopwatch.StartNew();
						bool flag2 = WriteMixedBatchWithRetry(conn, sqliteCommand, aggregateCmd, sqliteCommand2, list, list2, list3, list4, token);
						stopwatch.Stop();
						if (!flag2)
						{
							break;
						}
						flag = false;
						Interlocked.Add(ref writtenEvents, num4);
						Interlocked.Add(ref writtenEntityEvents, list2.Count);
						Interlocked.Add(ref writtenEntityAggregates, list3.Count);
						Interlocked.Add(ref writtenSnapshots, list4.Count);
						Interlocked.Exchange(ref lastBatchRows, num5);
						Interlocked.Exchange(ref lastBatchEntityRows, list2.Count);
						Interlocked.Exchange(ref lastBatchElapsedMs, stopwatch.ElapsedMilliseconds);
						Interlocked.Increment(ref writerBatchCount);
						Interlocked.Add(ref writerBatchTotalMs, stopwatch.ElapsedMilliseconds);
						long num6;
						do
						{
							num6 = Interlocked.Read(in writerBatchMaxMs);
						}
						while (stopwatch.ElapsedMilliseconds > num6 && Interlocked.CompareExchange(ref writerBatchMaxMs, stopwatch.ElapsedMilliseconds, num6) != num6);
						long num7 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
						recentBatchMetrics.Enqueue(new StorageBatchMetric(num7, num5, stopwatch.ElapsedMilliseconds));
						Interlocked.Increment(ref recentBatchMetricCount);
						StorageBatchMetric result10;
						StorageBatchMetric result11;
						while ((Volatile.Read(in recentBatchMetricCount) > 512 || (recentBatchMetrics.TryPeek(out result10) && result10.UnixMilliseconds < num7 - 300000)) && recentBatchMetrics.TryDequeue(out result11))
						{
							Interlocked.Decrement(ref recentBatchMetricCount);
						}
						SuiteTimingRegistry.Record("BlockLogger writer batch", stopwatch.Elapsed.TotalMilliseconds);
						MarkStorageSuccess();
					}
					Volatile.Write(ref recoveryWriterInFlight, 0);
				}
				while ((PendingEvents > 0 || !entityAggregateQueue.IsEmpty || Interlocked.Read(in queuedSnapshots) > 0) && !token.IsCancellationRequested);
				if (!token.IsCancellationRequested)
				{
					Thread.Sleep(1);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				MarkStorageError(ex2);
				api.Logger.Error("[BlockLogger] Event storage worker failed: {0}", ex2);
				Thread.Sleep(100);
			}
		}
		if (flag && list.Count + list2.Count + list3.Count + list4.Count > 0)
		{
			try
			{
				writeGate.Wait();
				try
				{
					InsertMixedWriterBatchPrepared(conn, sqliteCommand, aggregateCmd, sqliteCommand2, list, list2, list3, list4);
				}
				finally
				{
					writeGate.Release();
				}
				int num8 = list.Count + list2.Count;
				Interlocked.Add(ref writtenEvents, num8);
				Interlocked.Add(ref writtenEntityEvents, list2.Count);
				Interlocked.Add(ref writtenEntityAggregates, list3.Count);
				Interlocked.Add(ref writtenSnapshots, list4.Count);
				MarkStorageSuccess();
				flag = false;
			}
			catch (Exception ex3)
			{
				MarkStorageError(ex3);
				api.Logger.Error("[BlockLogger] Final locally-retained writer batch flush failed; {0} accepted row(s) could not be persisted: {1}: {2}", list.Count + list2.Count + list3.Count + list4.Count, ex3.GetType().Name, ex3.Message);
			}
		}
		DrainEventsPrepared(conn, sqliteCommand, aggregateCmd, list, list2, list3);
		DrainSnapshotsPrepared(conn, sqliteCommand2, list4);
	}

	private void QueryLoop(CancellationToken token)
	{
		try
		{
			migrationTask.GetAwaiter().GetResult();
			using SqliteConnection conn = OpenReadOnlyConnection();
			foreach (EventRequest item in queryQueue.GetConsumingEnumerable(token))
			{
				try
				{
					long num = Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - item.EnqueuedUnixMilliseconds);
					SuiteTimingRegistry.Record("BlockLogger query queue wait", num);
					Stopwatch stopwatch = Stopwatch.StartNew();
					try
					{
						item.Completion.TrySetResult(QueryEvents(conn, item));
					}
					finally
					{
						stopwatch.Stop();
						LogStorageService logStorageService = this;
						string kind;
						if (item.PlayerOnly)
						{
							kind = "player-events";
						}
						else
						{
							kind = (item.EntityOnly ? "entity-events" : "events");
						}
						logStorageService.RecordQueryMetric(kind, stopwatch.ElapsedMilliseconds);
						SuiteTimingRegistry.Record("BlockLogger query execution", stopwatch.Elapsed.TotalMilliseconds);
					}
				}
				catch (Exception ex)
				{
					MarkStorageError(ex);
					api.Logger.Error("[BlockLogger] Event query failed: {0}", ex);
					item.Completion.TrySetException(ex);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex3)
		{
			MarkStorageError(ex3);
			api.Logger.Error("[BlockLogger] Query worker stopped: {0}", ex3);
		}
		finally
		{
			StopEventQueries();
		}
	}

	private void SnapshotQueryLoop(CancellationToken token)
	{
		try
		{
			migrationTask.GetAwaiter().GetResult();
			using SqliteConnection conn = OpenReadOnlyConnection();
			foreach (SnapshotRequest item in snapshotQueryQueue.GetConsumingEnumerable(token))
			{
				try
				{
					long num = Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - item.EnqueuedUnixMilliseconds);
					SuiteTimingRegistry.Record("BlockLogger snapshot query queue wait", num);
					Stopwatch stopwatch = Stopwatch.StartNew();
					try
					{
						item.Completion.TrySetResult(QuerySnapshots(conn, item));
					}
					finally
					{
						stopwatch.Stop();
						RecordQueryMetric("container-snapshots", stopwatch.ElapsedMilliseconds);
						SuiteTimingRegistry.Record("BlockLogger snapshot query execution", stopwatch.Elapsed.TotalMilliseconds);
					}
				}
				catch (Exception ex)
				{
					MarkStorageError(ex);
					api.Logger.Error("[BlockLogger] Snapshot query failed: {0}", ex);
					item.Completion.TrySetException(ex);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex3)
		{
			MarkStorageError(ex3);
			api.Logger.Error("[BlockLogger] Snapshot query worker stopped: {0}", ex3);
		}
		finally
		{
			StopSnapshotQueries();
		}
	}

	private bool TryAddQuery<T>(BlockingCollection<T> queue, T request)
	{
		if (Volatile.Read(in disposeState) != 0)
		{
			return false;
		}
		try
		{
			return !queue.IsAddingCompleted && queue.TryAdd(request);
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	private void StopEventQueries()
	{
		try
		{
			queryQueue.CompleteAdding();
			EventRequest item;
			while (queryQueue.TryTake(out item))
			{
				item.Completion.TrySetException(new InvalidOperationException("BlockLogger event query worker stopped before this request ran."));
			}
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private void StopSnapshotQueries()
	{
		try
		{
			snapshotQueryQueue.CompleteAdding();
			SnapshotRequest item;
			while (snapshotQueryQueue.TryTake(out item))
			{
				item.Completion.TrySetException(new InvalidOperationException("BlockLogger snapshot query worker stopped before this request ran."));
			}
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private List<LogEventRecord> QueryEvents(SqliteConnection conn, EventRequest r)
	{
		if (r.PlayerOnly)
		{
			return QueryPlayerEvents(conn, r);
		}
		int num = Math.Max(0, r.Radius);
		int num2 = r.X - num;
		int num3 = r.X + num;
		int num4 = r.Y - num;
		int num5 = r.Y + num;
		int num6 = r.Z - num;
		int num7 = r.Z + num;
		StringBuilder stringBuilder = new StringBuilder("\nSELECT timestamp_ticks, ingame_date, action, player_uid, player_name, world_id,\n       dimension, x, y, z, block_code, meta_json\nFROM events\nWHERE world_id = $worldId\n  AND (dimension = $dimension OR dimension = $legacyDimension)\n  AND x BETWEEN $minX AND $maxX\n  AND y BETWEEN $minY AND $maxY\n  AND z BETWEEN $minZ AND $maxZ ");
		if (r.EntityOnly)
		{
			stringBuilder.Append("AND action IN (7,8,9,10) ");
		}
		else
		{
			stringBuilder.Append("AND action IN (1,2,3,4,5,6) ");
		}
		if (!string.IsNullOrWhiteSpace(r.Tag))
		{
			string text = r.Tag.Trim();
			if (text.Equals("block", StringComparison.OrdinalIgnoreCase))
			{
				stringBuilder.Append("AND action IN (1,2,3) ");
			}
			else if (text.Equals("container", StringComparison.OrdinalIgnoreCase))
			{
				stringBuilder.Append("AND action IN (4,5,6) ");
			}
			else if (text.Equals("entity", StringComparison.OrdinalIgnoreCase))
			{
				stringBuilder.Append("AND action IN (7,8,9,10) ");
			}
			else if (text.Equals("item", StringComparison.OrdinalIgnoreCase))
			{
				stringBuilder.Append("AND (meta_json LIKE '%itemCode%' OR meta_json LIKE '%heldItem%') ");
			}
			else
			{
				stringBuilder.Append("AND action = $tagAction ");
			}
		}
		if (!string.IsNullOrWhiteSpace(r.Text))
		{
			stringBuilder.Append("AND (\n player_name LIKE $text COLLATE NOCASE OR\n player_uid LIKE $text COLLATE NOCASE OR\n block_code LIKE $text COLLATE NOCASE OR\n ingame_date LIKE $text COLLATE NOCASE OR\n meta_json LIKE $text COLLATE NOCASE\n) ");
		}
		stringBuilder.Append("AND ((x - $cx) * (x - $cx) + (y - $cy) * (y - $cy) + (z - $cz) * (z - $cz)) <= $radiusSq ");
		stringBuilder.Append("ORDER BY timestamp_ticks DESC LIMIT $limit OFFSET $offset;");
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = stringBuilder.ToString();
		sqliteCommand.Parameters.AddWithValue("$worldId", currentWorldId);
		sqliteCommand.Parameters.AddWithValue("$dimension", r.Dimension.ToString());
		sqliteCommand.Parameters.AddWithValue("$legacyDimension", LegacyDimensionName(r.Dimension));
		sqliteCommand.Parameters.AddWithValue("$minX", num2);
		sqliteCommand.Parameters.AddWithValue("$maxX", num3);
		sqliteCommand.Parameters.AddWithValue("$minY", num4);
		sqliteCommand.Parameters.AddWithValue("$maxY", num5);
		sqliteCommand.Parameters.AddWithValue("$minZ", num6);
		sqliteCommand.Parameters.AddWithValue("$maxZ", num7);
		sqliteCommand.Parameters.AddWithValue("$cx", r.X);
		sqliteCommand.Parameters.AddWithValue("$cy", r.Y);
		sqliteCommand.Parameters.AddWithValue("$cz", r.Z);
		sqliteCommand.Parameters.AddWithValue("$radiusSq", num * num);
		int num8 = Math.Clamp(r.MaxCount, 1, 200);
		int num9 = (r.EntityOnly ? Math.Min(2200, num8 + Math.Clamp(r.Offset, 0, 2000)) : num8);
		sqliteCommand.Parameters.AddWithValue("$limit", num9);
		sqliteCommand.Parameters.AddWithValue("$offset", (!r.EntityOnly) ? Math.Clamp(r.Offset, 0, 2000) : 0);
		if (!string.IsNullOrWhiteSpace(r.Text))
		{
			sqliteCommand.Parameters.AddWithValue("$text", "%" + r.Text.Trim() + "%");
		}
		if (!string.IsNullOrWhiteSpace(r.Tag) && !r.Tag.Equals("block", StringComparison.OrdinalIgnoreCase) && !r.Tag.Equals("container", StringComparison.OrdinalIgnoreCase) && !r.Tag.Equals("entity", StringComparison.OrdinalIgnoreCase) && !r.Tag.Equals("item", StringComparison.OrdinalIgnoreCase))
		{
			if (!TryParseActionTag(r.Tag, out var action))
			{
				return new List<LogEventRecord>();
			}
			sqliteCommand.Parameters.AddWithValue("$tagAction", (int)action);
		}
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		List<LogEventRecord> list = new List<LogEventRecord>();
		while (sqliteDataReader.Read())
		{
			list.Add(ReadEvent(sqliteDataReader));
		}
		sqliteDataReader.Close();
		if (r.EntityOnly)
		{
			list.AddRange(QueryEntityActivity(conn, r, num2, num3, num4, num5, num6, num7, num));
			return list.OrderByDescending((LogEventRecord x) => x.TimestampUtc.Ticks).Skip(Math.Clamp(r.Offset, 0, 2000)).Take(Math.Clamp(r.MaxCount, 1, 200))
				.ToList();
		}
		return list;
	}

	private List<LogEventRecord> QueryPlayerEvents(SqliteConnection conn, EventRequest r)
	{
		string text = (r.Player ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return new List<LogEventRecord>();
		}
		StringBuilder stringBuilder = new StringBuilder("\nSELECT timestamp_ticks, ingame_date, action, player_uid, player_name, world_id,\n       dimension, x, y, z, block_code, meta_json\nFROM events\nWHERE world_id = $worldId\n  AND player_uid <> ''\n  AND " + (r.ExactUid ? "player_uid = $player COLLATE BINARY " : "(\n      player_uid = $player COLLATE NOCASE OR\n      player_name = $player COLLATE NOCASE OR\n      player_name LIKE $playerPrefix COLLATE NOCASE\n  ) "));
		switch (r.Tag?.Trim().ToLowerInvariant())
		{
		case "blocks":
			stringBuilder.Append("AND action IN (1,2,3) ");
			break;
		case "containers":
			stringBuilder.Append("AND action IN (4,5,6) ");
			break;
		case "entities":
			stringBuilder.Append("AND action IN (7,8,9,10) ");
			break;
		}
		stringBuilder.Append("ORDER BY timestamp_ticks DESC LIMIT $limit OFFSET $offset;");
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = stringBuilder.ToString();
		sqliteCommand.Parameters.AddWithValue("$worldId", currentWorldId);
		sqliteCommand.Parameters.AddWithValue("$player", text);
		if (!r.ExactUid)
		{
			sqliteCommand.Parameters.AddWithValue("$playerPrefix", text + "%");
		}
		sqliteCommand.Parameters.AddWithValue("$limit", Math.Clamp(r.MaxCount, 1, 200));
		sqliteCommand.Parameters.AddWithValue("$offset", Math.Clamp(r.Offset, 0, 2000));
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		List<LogEventRecord> list = new List<LogEventRecord>();
		while (sqliteDataReader.Read())
		{
			list.Add(ReadEvent(sqliteDataReader));
		}
		return list;
	}

	private List<LogEventRecord> QueryEntityActivity(SqliteConnection conn, EventRequest r, int minX, int maxX, int minY, int maxY, int minZ, int maxZ, int radius)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		StringBuilder stringBuilder = new StringBuilder("\nSELECT window_end_ticks, action, world_id, dimension, sample_x, sample_y, sample_z,\n       entity_code, entity_class, event_count, region_x, region_z\nFROM entity_activity\nWHERE world_id = $worldId\n  AND (dimension = $dimension OR dimension = $legacyDimension)\n  AND sample_x BETWEEN $minX AND $maxX\n  AND sample_y BETWEEN $minY AND $maxY\n  AND sample_z BETWEEN $minZ AND $maxZ\n  AND ((sample_x - $cx) * (sample_x - $cx) + (sample_y - $cy) * (sample_y - $cy) + (sample_z - $cz) * (sample_z - $cz)) <= $radiusSq ");
		if (!string.IsNullOrWhiteSpace(r.Text))
		{
			stringBuilder.Append("AND (entity_code LIKE $text COLLATE NOCASE OR entity_class LIKE $text COLLATE NOCASE) ");
		}
		int? num = null;
		if (!string.IsNullOrWhiteSpace(r.Tag) && !r.Tag.Equals("entity", StringComparison.OrdinalIgnoreCase))
		{
			bool flag = TryParseActionTag(r.Tag, out var action);
			bool flag2 = flag;
			if (flag2)
			{
				bool flag3 = ((action == LogActionType.EntitySpawn || action == LogActionType.EntityDespawn) ? true : false);
				flag2 = flag3;
			}
			if (!flag2)
			{
				return new List<LogEventRecord>();
			}
			num = (int)action;
			stringBuilder.Append("AND action = $action ");
		}
		stringBuilder.Append("ORDER BY window_end_ticks DESC LIMIT $limit;");
		sqliteCommand.CommandText = stringBuilder.ToString();
		sqliteCommand.Parameters.AddWithValue("$worldId", currentWorldId);
		sqliteCommand.Parameters.AddWithValue("$dimension", r.Dimension.ToString());
		sqliteCommand.Parameters.AddWithValue("$legacyDimension", LegacyDimensionName(r.Dimension));
		sqliteCommand.Parameters.AddWithValue("$minX", minX);
		sqliteCommand.Parameters.AddWithValue("$maxX", maxX);
		sqliteCommand.Parameters.AddWithValue("$minY", minY);
		sqliteCommand.Parameters.AddWithValue("$maxY", maxY);
		sqliteCommand.Parameters.AddWithValue("$minZ", minZ);
		sqliteCommand.Parameters.AddWithValue("$maxZ", maxZ);
		sqliteCommand.Parameters.AddWithValue("$cx", r.X);
		sqliteCommand.Parameters.AddWithValue("$cy", r.Y);
		sqliteCommand.Parameters.AddWithValue("$cz", r.Z);
		sqliteCommand.Parameters.AddWithValue("$radiusSq", radius * radius);
		sqliteCommand.Parameters.AddWithValue("$limit", Math.Min(2200, Math.Clamp(r.MaxCount, 1, 200) + Math.Clamp(r.Offset, 0, 2000)));
		if (!string.IsNullOrWhiteSpace(r.Text))
		{
			sqliteCommand.Parameters.AddWithValue("$text", "%" + r.Text.Trim() + "%");
		}
		if (num.HasValue)
		{
			sqliteCommand.Parameters.AddWithValue("$action", num.Value);
		}
		List<LogEventRecord> list = new List<LogEventRecord>();
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			LogEventRecord logEventRecord = new LogEventRecord
			{
				TimestampUtc = new DateTime(sqliteDataReader.GetInt64(0), DateTimeKind.Utc),
				IngameDate = "aggregate",
				Action = (LogActionType)sqliteDataReader.GetInt32(1),
				PlayerUid = "",
				PlayerName = "System",
				WorldId = (sqliteDataReader.IsDBNull(2) ? "" : sqliteDataReader.GetString(2)),
				Dimension = (sqliteDataReader.IsDBNull(3) ? "" : sqliteDataReader.GetString(3)),
				X = sqliteDataReader.GetInt32(4),
				Y = sqliteDataReader.GetInt32(5),
				Z = sqliteDataReader.GetInt32(6),
				BlockCode = ""
			};
			logEventRecord.Meta["entityCode"] = (sqliteDataReader.IsDBNull(7) ? "entity" : sqliteDataReader.GetString(7));
			logEventRecord.Meta["entityClass"] = (sqliteDataReader.IsDBNull(8) ? "" : sqliteDataReader.GetString(8));
			logEventRecord.Meta["eventCount"] = sqliteDataReader.GetInt64(9).ToString();
			logEventRecord.Meta["aggregate"] = "true";
			logEventRecord.Meta["regionX"] = sqliteDataReader.GetInt32(10).ToString();
			logEventRecord.Meta["regionZ"] = sqliteDataReader.GetInt32(11).ToString();
			list.Add(logEventRecord);
		}
		return list;
	}

	private List<ContainerSnapshotRecord> QuerySnapshots(SqliteConnection conn, SnapshotRequest r)
	{
		int num = Math.Max(0, r.Radius);
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "\nSELECT timestamp_ticks, ingame_date, world_id, dimension, x, y, z, block_code,\n       player_uid, player_name, inventory_id, reason, changed_slot_id,\n       slot_count, non_empty_slot_count, total_item_count, slots_json\nFROM snapshots\nWHERE world_id = $worldId\n  AND (dimension = $dimension OR dimension = $legacyDimension)\n  AND x BETWEEN $minX AND $maxX\n  AND y BETWEEN $minY AND $maxY\n  AND z BETWEEN $minZ AND $maxZ\n  AND ((x - $cx) * (x - $cx) + (y - $cy) * (y - $cy) + (z - $cz) * (z - $cz)) <= $radiusSq\n  AND (\n    $text = '' OR player_name LIKE $text COLLATE NOCASE OR\n    player_uid LIKE $text COLLATE NOCASE OR block_code LIKE $text COLLATE NOCASE OR\n    reason LIKE $text COLLATE NOCASE OR inventory_id LIKE $text COLLATE NOCASE\n  )\nORDER BY timestamp_ticks DESC\nLIMIT $limit;";
		sqliteCommand.Parameters.AddWithValue("$worldId", currentWorldId);
		sqliteCommand.Parameters.AddWithValue("$dimension", r.Dimension.ToString());
		sqliteCommand.Parameters.AddWithValue("$legacyDimension", LegacyDimensionName(r.Dimension));
		sqliteCommand.Parameters.AddWithValue("$minX", r.X - num);
		sqliteCommand.Parameters.AddWithValue("$maxX", r.X + num);
		sqliteCommand.Parameters.AddWithValue("$minY", r.Y - num);
		sqliteCommand.Parameters.AddWithValue("$maxY", r.Y + num);
		sqliteCommand.Parameters.AddWithValue("$minZ", r.Z - num);
		sqliteCommand.Parameters.AddWithValue("$maxZ", r.Z + num);
		sqliteCommand.Parameters.AddWithValue("$cx", r.X);
		sqliteCommand.Parameters.AddWithValue("$cy", r.Y);
		sqliteCommand.Parameters.AddWithValue("$cz", r.Z);
		sqliteCommand.Parameters.AddWithValue("$radiusSq", num * num);
		sqliteCommand.Parameters.AddWithValue("$text", string.IsNullOrWhiteSpace(r.Text) ? "" : ("%" + r.Text.Trim() + "%"));
		sqliteCommand.Parameters.AddWithValue("$limit", Math.Clamp(r.MaxCount, 1, 100));
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		List<ContainerSnapshotRecord> list = new List<ContainerSnapshotRecord>();
		while (sqliteDataReader.Read())
		{
			list.Add(ReadSnapshot(sqliteDataReader));
		}
		return list;
	}

	private void InsertEvents(List<LogEventRecord> records)
	{
		using SqliteConnection conn = OpenConnection();
		using SqliteCommand cmd = CreatePreparedEventInsert(conn);
		InsertEventsPrepared(conn, cmd, records);
	}

	private void InsertSnapshots(List<ContainerSnapshotRecord> records)
	{
		using SqliteConnection conn = OpenConnection();
		using SqliteCommand cmd = CreatePreparedSnapshotInsert(conn);
		InsertSnapshotsPrepared(conn, cmd, records);
	}

	private static SqliteCommand CreatePreparedEventInsert(SqliteConnection conn)
	{
		SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "\nINSERT INTO events\n(timestamp_ticks,timestamp_utc,ingame_date,action,player_uid,player_name,world_id,dimension,x,y,z,block_code,meta_json)\nVALUES\n($ticks,$utc,$ingame,$action,$uid,$name,$world,$dimension,$x,$y,$z,$block,$meta);";
		sqliteCommand.Parameters.Add("$ticks", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$utc", SqliteType.Text);
		sqliteCommand.Parameters.Add("$ingame", SqliteType.Text);
		sqliteCommand.Parameters.Add("$action", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$uid", SqliteType.Text);
		sqliteCommand.Parameters.Add("$name", SqliteType.Text);
		sqliteCommand.Parameters.Add("$world", SqliteType.Text);
		sqliteCommand.Parameters.Add("$dimension", SqliteType.Text);
		sqliteCommand.Parameters.Add("$x", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$y", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$z", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$block", SqliteType.Text);
		sqliteCommand.Parameters.Add("$meta", SqliteType.Text);
		sqliteCommand.Prepare();
		return sqliteCommand;
	}

	private static SqliteCommand CreatePreparedEntityAggregateInsert(SqliteConnection conn)
	{
		SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "\nINSERT INTO entity_activity\n(window_start_ticks,window_end_ticks,action,entity_type_id,entity_code,entity_class,event_count,world_id,dimension,sample_x,sample_y,sample_z,region_x,region_z)\nVALUES ($start,$end,$action,$type,$code,$class,$count,$world,$dimension,$x,$y,$z,$regionX,$regionZ);";
		sqliteCommand.Parameters.Add("$start", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$end", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$action", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$type", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$code", SqliteType.Text);
		sqliteCommand.Parameters.Add("$class", SqliteType.Text);
		sqliteCommand.Parameters.Add("$count", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$world", SqliteType.Text);
		sqliteCommand.Parameters.Add("$dimension", SqliteType.Text);
		sqliteCommand.Parameters.Add("$x", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$y", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$z", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$regionX", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$regionZ", SqliteType.Integer);
		sqliteCommand.Prepare();
		return sqliteCommand;
	}

	private static void InsertEntityAggregatesPrepared(SqliteConnection conn, SqliteCommand cmd, List<EntityActivityAggregate> records)
	{
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		cmd.Transaction = sqliteTransaction;
		try
		{
			foreach (EntityActivityAggregate record in records)
			{
				SetEntityAggregateParameters(cmd, record);
				cmd.ExecuteNonQuery();
			}
			sqliteTransaction.Commit();
		}
		finally
		{
			cmd.Transaction = null;
		}
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
		SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "\nINSERT INTO snapshots\n(timestamp_ticks,timestamp_utc,ingame_date,world_id,dimension,x,y,z,block_code,\n player_uid,player_name,inventory_id,reason,changed_slot_id,slot_count,\n non_empty_slot_count,total_item_count,slots_json)\nVALUES\n($ticks,$utc,$ingame,$world,$dimension,$x,$y,$z,$block,$uid,$name,$inv,$reason,\n $slot,$slotCount,$nonEmpty,$total,$slots);";
		sqliteCommand.Parameters.Add("$ticks", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$utc", SqliteType.Text);
		sqliteCommand.Parameters.Add("$ingame", SqliteType.Text);
		sqliteCommand.Parameters.Add("$world", SqliteType.Text);
		sqliteCommand.Parameters.Add("$dimension", SqliteType.Text);
		sqliteCommand.Parameters.Add("$x", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$y", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$z", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$block", SqliteType.Text);
		sqliteCommand.Parameters.Add("$uid", SqliteType.Text);
		sqliteCommand.Parameters.Add("$name", SqliteType.Text);
		sqliteCommand.Parameters.Add("$inv", SqliteType.Text);
		sqliteCommand.Parameters.Add("$reason", SqliteType.Text);
		sqliteCommand.Parameters.Add("$slot", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$slotCount", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$nonEmpty", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$total", SqliteType.Integer);
		sqliteCommand.Parameters.Add("$slots", SqliteType.Text);
		sqliteCommand.Prepare();
		return sqliteCommand;
	}

	private static void InsertMixedEventsPrepared(SqliteConnection conn, SqliteCommand cmd, List<QueuedEventPayload> genericRecords, List<QueuedEntityEventPayload> entityRecords)
	{
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		cmd.Transaction = sqliteTransaction;
		foreach (QueuedEventPayload genericRecord in genericRecords)
		{
			SetQueuedEventParameters(cmd, genericRecord);
			cmd.ExecuteNonQuery();
		}
		foreach (QueuedEntityEventPayload entityRecord in entityRecords)
		{
			SetQueuedEntityEventParameters(cmd, entityRecord);
			cmd.ExecuteNonQuery();
		}
		sqliteTransaction.Commit();
		cmd.Transaction = null;
	}

	private static void InsertMixedWriterBatchPrepared(SqliteConnection conn, SqliteCommand eventCmd, SqliteCommand aggregateCmd, SqliteCommand snapshotCmd, List<QueuedEventPayload> genericRecords, List<QueuedEntityEventPayload> entityRecords, List<EntityActivityAggregate> aggregateRecords, List<ContainerSnapshotRecord> snapshotRecords)
	{
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		eventCmd.Transaction = sqliteTransaction;
		aggregateCmd.Transaction = sqliteTransaction;
		snapshotCmd.Transaction = sqliteTransaction;
		try
		{
			foreach (QueuedEventPayload genericRecord in genericRecords)
			{
				SetQueuedEventParameters(eventCmd, genericRecord);
				eventCmd.ExecuteNonQuery();
			}
			foreach (QueuedEntityEventPayload entityRecord in entityRecords)
			{
				SetQueuedEntityEventParameters(eventCmd, entityRecord);
				eventCmd.ExecuteNonQuery();
			}
			foreach (EntityActivityAggregate aggregateRecord in aggregateRecords)
			{
				SetEntityAggregateParameters(aggregateCmd, aggregateRecord);
				aggregateCmd.ExecuteNonQuery();
			}
			foreach (ContainerSnapshotRecord snapshotRecord in snapshotRecords)
			{
				SetSnapshotParameters(snapshotCmd, snapshotRecord);
				snapshotCmd.ExecuteNonQuery();
			}
			sqliteTransaction.Commit();
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
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		cmd.Transaction = sqliteTransaction;
		foreach (QueuedEventPayload record in records)
		{
			SetQueuedEventParameters(cmd, record);
			cmd.ExecuteNonQuery();
		}
		sqliteTransaction.Commit();
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
		cmd.Parameters["$meta"].Value = BuildEntityMetaJson(in r);
	}

	private static string BuildEntityMetaJson(in QueuedEntityEventPayload r)
	{
		StringBuilder stringBuilder = new StringBuilder(192);
		stringBuilder.Append('{');
		bool first = true;
		AppendMetaPair(stringBuilder, ref first, "entityId", r.EntityId.ToString());
		AppendMetaPair(stringBuilder, ref first, "entityTypeId", r.EntityTypeId.ToString());
		AppendMetaPair(stringBuilder, ref first, "entityCode", r.EntityCode);
		AppendMetaPair(stringBuilder, ref first, "entityClass", r.EntityClass);
		if (r.EventCount > 1)
		{
			AppendMetaPair(stringBuilder, ref first, "eventCount", r.EventCount.ToString());
		}
		if (!string.IsNullOrEmpty(r.HeldItem))
		{
			AppendMetaPair(stringBuilder, ref first, "heldItem", r.HeldItem);
			AppendMetaPair(stringBuilder, ref first, "heldStackSize", r.HeldStackSize.ToString());
		}
		if (r.DamageTier != int.MinValue)
		{
			AppendMetaPair(stringBuilder, ref first, "damageTier", r.DamageTier.ToString());
		}
		if (!string.IsNullOrEmpty(r.DamageType))
		{
			AppendMetaPair(stringBuilder, ref first, "damageType", r.DamageType);
		}
		if (r.SourceEntityId >= 0)
		{
			AppendMetaPair(stringBuilder, ref first, "sourceEntityId", r.SourceEntityId.ToString());
		}
		if (!string.IsNullOrEmpty(r.SourceEntityCode))
		{
			AppendMetaPair(stringBuilder, ref first, "sourceEntityCode", r.SourceEntityCode);
		}
		if (!string.IsNullOrEmpty(r.SourcePlayerUid))
		{
			AppendMetaPair(stringBuilder, ref first, "sourcePlayerUid", r.SourcePlayerUid);
		}
		if (!string.IsNullOrEmpty(r.SourcePlayerName))
		{
			AppendMetaPair(stringBuilder, ref first, "sourcePlayerName", r.SourcePlayerName);
		}
		if (r.SourceX.HasValue && r.SourceY.HasValue && r.SourceZ.HasValue)
		{
			AppendMetaPair(stringBuilder, ref first, "sourcePos", $"{r.SourceX.Value:0.##},{r.SourceY.Value:0.##},{r.SourceZ.Value:0.##}");
		}
		if (!string.IsNullOrEmpty(r.DespawnReason))
		{
			AppendMetaPair(stringBuilder, ref first, "despawnReason", r.DespawnReason);
		}
		stringBuilder.Append('}');
		return stringBuilder.ToString();
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
		cmd.Parameters["$meta"].Value = BuildMetaJson(in r);
	}

	private static string BuildMetaJson(in QueuedEventPayload r)
	{
		if (r.K1 == null && r.K2 == null && r.K3 == null && r.K4 == null && r.K5 == null && r.K6 == null && r.K7 == null && r.K8 == null)
		{
			return "{}";
		}
		StringBuilder stringBuilder = new StringBuilder(160);
		stringBuilder.Append('{');
		bool first = true;
		AppendMetaPair(stringBuilder, ref first, r.K1, r.V1);
		AppendMetaPair(stringBuilder, ref first, r.K2, r.V2);
		AppendMetaPair(stringBuilder, ref first, r.K3, r.V3);
		AppendMetaPair(stringBuilder, ref first, r.K4, r.V4);
		AppendMetaPair(stringBuilder, ref first, r.K5, r.V5);
		AppendMetaPair(stringBuilder, ref first, r.K6, r.V6);
		AppendMetaPair(stringBuilder, ref first, r.K7, r.V7);
		AppendMetaPair(stringBuilder, ref first, r.K8, r.V8);
		stringBuilder.Append('}');
		return stringBuilder.ToString();
	}

	private static void AppendMetaPair(StringBuilder sb, ref bool first, string? key, string? value)
	{
		if (!string.IsNullOrEmpty(key) && value != null)
		{
			if (!first)
			{
				sb.Append(',');
			}
			first = false;
			AppendJsonString(sb, key);
			sb.Append(':');
			AppendJsonString(sb, value);
		}
	}

	private static void AppendJsonString(StringBuilder sb, string value)
	{
		sb.Append('"');
		foreach (char c in value)
		{
			switch (c)
			{
			case '"':
				sb.Append("\\\"");
				continue;
			case '\\':
				sb.Append("\\\\");
				continue;
			case '\b':
				sb.Append("\\b");
				continue;
			case '\f':
				sb.Append("\\f");
				continue;
			case '\n':
				sb.Append("\\n");
				continue;
			case '\r':
				sb.Append("\\r");
				continue;
			case '\t':
				sb.Append("\\t");
				continue;
			}
			if (c < ' ')
			{
				StringBuilder stringBuilder = sb.Append("\\u");
				int num = c;
				stringBuilder.Append(num.ToString("x4"));
			}
			else
			{
				sb.Append(c);
			}
		}
		sb.Append('"');
	}

	private static void InsertEventsPrepared(SqliteConnection conn, SqliteCommand cmd, List<LogEventRecord> records)
	{
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		cmd.Transaction = sqliteTransaction;
		foreach (LogEventRecord record in records)
		{
			SetEventParameters(cmd, record);
			cmd.ExecuteNonQuery();
		}
		sqliteTransaction.Commit();
		cmd.Transaction = null;
	}

	private static void InsertSnapshotsPrepared(SqliteConnection conn, SqliteCommand cmd, List<ContainerSnapshotRecord> records)
	{
		using SqliteTransaction sqliteTransaction = conn.BeginTransaction();
		cmd.Transaction = sqliteTransaction;
		foreach (ContainerSnapshotRecord record in records)
		{
			SetSnapshotParameters(cmd, record);
			cmd.ExecuteNonQuery();
		}
		sqliteTransaction.Commit();
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
		cmd.Parameters["$slot"].Value = ((object)r.ChangedSlotId) ?? DBNull.Value;
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
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			MarkStorageError(ex2);
			api.Logger.Error("[BlockLogger] Legacy log migration failed: {0}", ex2);
			migrationComplete = true;
		}
	}

	private void MigrateLegacyJsonl(CancellationToken token)
	{
		WithConnection((SqliteConnection conn) =>
		{
			List<string> list = (Directory.Exists(eventLogDir) ? Directory.EnumerateFiles(eventLogDir, "*.jsonl").OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>());
			foreach (string item in list)
			{
				token.ThrowIfCancellationRequested();
				if (!IsImported(conn, "event:" + item))
				{
					List<LogEventRecord> list2 = new List<LogEventRecord>(500);
					foreach (string item2 in File.ReadLines(item))
					{
						token.ThrowIfCancellationRequested();
						if (!string.IsNullOrWhiteSpace(item2))
						{
							try
							{
								LogEventRecord logEventRecord = JsonConvert.DeserializeObject<LogEventRecord>(item2);
								if (logEventRecord != null)
								{
									list2.Add(logEventRecord);
								}
								if (list2.Count >= 500)
								{
									ExecuteMigrationEventBatch(conn, list2);
									list2.Clear();
								}
							}
							catch
							{
							}
						}
					}
					if (list2.Count > 0)
					{
						ExecuteMigrationEventBatch(conn, list2);
					}
					MarkImported(conn, "event:" + item);
				}
			}
			List<string> list3 = (Directory.Exists(snapshotLogDir) ? Directory.EnumerateFiles(snapshotLogDir, "*.jsonl").OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>());
			foreach (string item3 in list3)
			{
				token.ThrowIfCancellationRequested();
				if (!IsImported(conn, "snapshot:" + item3))
				{
					List<ContainerSnapshotRecord> list4 = new List<ContainerSnapshotRecord>(100);
					foreach (string item4 in File.ReadLines(item3))
					{
						token.ThrowIfCancellationRequested();
						if (!string.IsNullOrWhiteSpace(item4))
						{
							try
							{
								ContainerSnapshotRecord containerSnapshotRecord = JsonConvert.DeserializeObject<ContainerSnapshotRecord>(item4);
								if (containerSnapshotRecord != null)
								{
									list4.Add(containerSnapshotRecord);
								}
								if (list4.Count >= 100)
								{
									ExecuteMigrationSnapshotBatch(conn, list4);
									list4.Clear();
								}
							}
							catch
							{
							}
						}
					}
					if (list4.Count > 0)
					{
						ExecuteMigrationSnapshotBatch(conn, list4);
					}
					MarkImported(conn, "snapshot:" + item3);
				}
			}
			return 0;
		});
	}

	private static bool IsImported(SqliteConnection conn, string key)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT 1 FROM meta WHERE key=$key LIMIT 1;";
		sqliteCommand.Parameters.AddWithValue("$key", key);
		return sqliteCommand.ExecuteScalar() != null;
	}

	private void MarkImported(SqliteConnection conn, string key)
	{
		writeGate.Wait(cts.Token);
		try
		{
			using SqliteCommand sqliteCommand = conn.CreateCommand();
			sqliteCommand.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES($key,$value);";
			sqliteCommand.Parameters.AddWithValue("$key", key);
			sqliteCommand.Parameters.AddWithValue("$value", DateTime.UtcNow.Ticks.ToString());
			sqliteCommand.ExecuteNonQuery();
		}
		finally
		{
			writeGate.Release();
		}
	}

	private void ExecuteMigrationEventBatch(SqliteConnection conn, List<LogEventRecord> records)
	{
		writeGate.Wait(cts.Token);
		try
		{
			InsertEventsUsingConnection(conn, records);
		}
		finally
		{
			writeGate.Release();
		}
		Thread.Sleep(1);
	}

	private void ExecuteMigrationSnapshotBatch(SqliteConnection conn, List<ContainerSnapshotRecord> records)
	{
		writeGate.Wait(cts.Token);
		try
		{
			InsertSnapshotsUsingConnection(conn, records);
		}
		finally
		{
			writeGate.Release();
		}
		Thread.Sleep(1);
	}

	private static void InsertEventsUsingConnection(SqliteConnection conn, List<LogEventRecord> records)
	{
		using SqliteCommand cmd = CreatePreparedEventInsert(conn);
		InsertEventsPrepared(conn, cmd, records);
	}

	private static void InsertSnapshotsUsingConnection(SqliteConnection conn, List<ContainerSnapshotRecord> records)
	{
		using SqliteCommand cmd = CreatePreparedSnapshotInsert(conn);
		InsertSnapshotsPrepared(conn, cmd, records);
	}

	private static string LegacyDimensionName(int dimension)
	{
		if (1 == 0)
		{
		}
		string result = dimension switch
		{
			0 => "overworld", 
			1 => "alternate1", 
			2 => "alternate2", 
			_ => $"dimension-{dimension}", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool TryParseActionTag(string value, out LogActionType action)
	{
		switch (value.Trim().ToLowerInvariant())
		{
		case "break":
			action = LogActionType.BlockBreak;
			return true;
		case "place":
			action = LogActionType.BlockPlace;
			return true;
		case "interact":
		case "use":
			action = LogActionType.BlockUse;
			return true;
		case "open":
			action = LogActionType.ContainerOpen;
			return true;
		case "take":
			action = LogActionType.ContainerTake;
			return true;
		case "put":
			action = LogActionType.ContainerPut;
			return true;
		case "entityinteract":
			action = LogActionType.EntityInteract;
			return true;
		case "entityspawn":
			action = LogActionType.EntitySpawn;
			return true;
		case "entitydeath":
			action = LogActionType.EntityDeath;
			return true;
		case "entitydespawn":
			action = LogActionType.EntityDespawn;
			return true;
		default:
			return Enum.TryParse<LogActionType>(value, ignoreCase: true, out action);
		}
	}

	private static LogEventRecord ReadEvent(SqliteDataReader reader)
	{
		LogEventRecord logEventRecord = new LogEventRecord
		{
			TimestampUtc = new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
			IngameDate = (reader.IsDBNull(1) ? "" : reader.GetString(1)),
			Action = (LogActionType)reader.GetInt32(2),
			PlayerUid = (reader.IsDBNull(3) ? "" : reader.GetString(3)),
			PlayerName = (reader.IsDBNull(4) ? "" : reader.GetString(4)),
			WorldId = (reader.IsDBNull(5) ? "" : reader.GetString(5)),
			Dimension = (reader.IsDBNull(6) ? "" : reader.GetString(6)),
			X = reader.GetInt32(7),
			Y = reader.GetInt32(8),
			Z = reader.GetInt32(9),
			BlockCode = (reader.IsDBNull(10) ? "" : reader.GetString(10))
		};
		if (!reader.IsDBNull(11))
		{
			try
			{
				logEventRecord.Meta = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.GetString(11)) ?? new Dictionary<string, string>();
			}
			catch
			{
			}
		}
		return logEventRecord;
	}

	private static ContainerSnapshotRecord ReadSnapshot(SqliteDataReader reader)
	{
		ContainerSnapshotRecord containerSnapshotRecord = new ContainerSnapshotRecord
		{
			TimestampUtc = new DateTime(reader.GetInt64(0), DateTimeKind.Utc),
			IngameDate = (reader.IsDBNull(1) ? "" : reader.GetString(1)),
			WorldId = (reader.IsDBNull(2) ? "" : reader.GetString(2)),
			Dimension = (reader.IsDBNull(3) ? "" : reader.GetString(3)),
			X = reader.GetInt32(4),
			Y = reader.GetInt32(5),
			Z = reader.GetInt32(6),
			BlockCode = (reader.IsDBNull(7) ? "" : reader.GetString(7)),
			PlayerUid = (reader.IsDBNull(8) ? "" : reader.GetString(8)),
			PlayerName = (reader.IsDBNull(9) ? "" : reader.GetString(9)),
			InventoryId = (reader.IsDBNull(10) ? "" : reader.GetString(10)),
			Reason = (reader.IsDBNull(11) ? "" : reader.GetString(11)),
			ChangedSlotId = (reader.IsDBNull(12) ? ((int?)null) : new int?(reader.GetInt32(12))),
			SlotCount = reader.GetInt32(13),
			NonEmptySlotCount = reader.GetInt32(14),
			TotalItemCount = reader.GetInt32(15)
		};
		if (!reader.IsDBNull(16))
		{
			try
			{
				containerSnapshotRecord.Slots = JsonConvert.DeserializeObject<List<ContainerSnapshotSlot>>(reader.GetString(16)) ?? new List<ContainerSnapshotSlot>();
			}
			catch
			{
			}
		}
		return containerSnapshotRecord;
	}

	private static void ObservePeak(ref long peak, long value)
	{
		long num = Interlocked.Read(in peak);
		while (value > num)
		{
			long num2 = Interlocked.CompareExchange(ref peak, value, num);
			if (num2 == num)
			{
				break;
			}
			num = num2;
		}
	}

	private static TaskCompletionSource<T> NewTcs<T>()
	{
		return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private SqliteConnection OpenConnection()
	{
		SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadWriteCreate;Cache=Private;Pooling=False");
		sqliteConnection.Open();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE; PRAGMA cache_size=-16384; PRAGMA mmap_size=0; PRAGMA wal_autocheckpoint=2000;";
		sqliteCommand.ExecuteNonQuery();
		return sqliteConnection;
	}

	private SqliteConnection OpenReadOnlyConnection()
	{
		SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly;Cache=Private;Pooling=False");
		sqliteConnection.Open();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=ON; PRAGMA temp_store=FILE; PRAGMA cache_size=-8192; PRAGMA mmap_size=0;";
		sqliteCommand.ExecuteNonQuery();
		return sqliteConnection;
	}

	private T WithConnection<T>(Func<SqliteConnection, T> action)
	{
		using SqliteConnection arg = OpenConnection();
		return action(arg);
	}

	private T WithReadOnlyConnection<T>(Func<SqliteConnection, T> action)
	{
		using SqliteConnection arg = OpenReadOnlyConnection();
		return action(arg);
	}

	private void DrainEventsPrepared(SqliteConnection conn, SqliteCommand cmd, SqliteCommand aggregateCmd, List<QueuedEventPayload> genericBatch, List<QueuedEntityEventPayload> entityBatch, List<EntityActivityAggregate> aggregateBatch)
	{
		while (true)
		{
			genericBatch.Clear();
			entityBatch.Clear();
			while (genericBatch.Count + entityBatch.Count < 5000)
			{
				if (criticalEventQueue.TryDequeue(out var result))
				{
					Interlocked.Decrement(ref queuedCriticalEvents);
					Interlocked.Decrement(ref queuedEvents);
					genericBatch.Add(result);
					continue;
				}
				if (criticalEntityQueue.TryDequeue(out var result2))
				{
					Interlocked.Decrement(ref queuedCriticalEvents);
					Interlocked.Decrement(ref queuedEvents);
					entityBatch.Add(result2);
					continue;
				}
				if (overflowEventQueue.TryDequeue(out var result3))
				{
					Interlocked.Decrement(ref queuedOverflowEvents);
					genericBatch.Add(result3);
					continue;
				}
				if (overflowEntityQueue.TryDequeue(out var result4))
				{
					Interlocked.Decrement(ref queuedOverflowEvents);
					entityBatch.Add(result4);
					continue;
				}
				if (bulkEntityQueue.TryDequeue(out var result5))
				{
					Interlocked.Decrement(ref queuedBulkEntityEvents);
					Interlocked.Decrement(ref queuedEvents);
					entityBatch.Add(result5);
					continue;
				}
				break;
			}
			int num = genericBatch.Count + entityBatch.Count;
			if (num == 0)
			{
				break;
			}
			try
			{
				writeGate.Wait();
				try
				{
					InsertMixedEventsPrepared(conn, cmd, genericBatch, entityBatch);
				}
				finally
				{
					writeGate.Release();
				}
				Interlocked.Add(ref writtenEvents, num);
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
			EntityActivityAggregate result6;
			while (aggregateBatch.Count < 256 && entityAggregateQueue.TryDequeue(out result6))
			{
				Interlocked.Decrement(ref queuedEntityAggregates);
				aggregateBatch.Add(result6);
			}
			if (aggregateBatch.Count == 0)
			{
				break;
			}
			try
			{
				writeGate.Wait();
				try
				{
					InsertEntityAggregatesPrepared(conn, aggregateCmd, aggregateBatch);
				}
				finally
				{
					writeGate.Release();
				}
				Interlocked.Add(ref writtenEntityAggregates, aggregateBatch.Count);
			}
			catch (Exception ex2)
			{
				MarkStorageError(ex2);
				api.Logger.Error("[BlockLogger] Final entity aggregate flush failed: {0}", ex2);
				break;
			}
		}
	}

	private void DrainSnapshotsPrepared(SqliteConnection conn, SqliteCommand cmd, List<ContainerSnapshotRecord> batch)
	{
		while (true)
		{
			batch.Clear();
			ContainerSnapshotRecord result;
			while (batch.Count < 500 && snapshotQueue.TryDequeue(out result))
			{
				batch.Add(result);
				Interlocked.Decrement(ref queuedSnapshots);
			}
			if (batch.Count == 0)
			{
				break;
			}
			try
			{
				writeGate.Wait();
				try
				{
					InsertSnapshotsPrepared(conn, cmd, batch);
				}
				finally
				{
					writeGate.Release();
				}
				Interlocked.Add(ref writtenSnapshots, batch.Count);
			}
			catch (Exception ex)
			{
				MarkStorageError(ex);
				api.Logger.Error("[BlockLogger] Final snapshot flush failed: {0}", ex);
				break;
			}
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposeState, 1) != 0)
		{
			return;
		}
		cts.Cancel();
		StopEventQueries();
		StopSnapshotQueries();
		try
		{
			eventSignal.Release();
		}
		catch
		{
		}
		bool flag = false;
		try
		{
			flag = Task.WaitAll(new Task[4] { eventWriterTask, queryTask, snapshotQueryTask, migrationTask }, 12000);
		}
		catch
		{
			flag = eventWriterTask.IsCompleted && queryTask.IsCompleted && snapshotQueryTask.IsCompleted && migrationTask.IsCompleted;
		}
		if (eventWriterTask.IsCompleted && Volatile.Read(in backupInProgress) == 0)
		{
			bool flag2 = false;
			try
			{
				flag2 = writeGate.Wait(TimeSpan.FromSeconds(2L));
				if (flag2)
				{
					Stopwatch stopwatch = Stopwatch.StartNew();
					WithConnection((SqliteConnection conn) =>
					{
						using SqliteCommand sqliteCommand = conn.CreateCommand();
						sqliteCommand.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);";
						sqliteCommand.ExecuteNonQuery();
						return 0;
					});
					stopwatch.Stop();
					SuiteTimingRegistry.Record("BlockLogger final SQLite maintenance", stopwatch.Elapsed.TotalMilliseconds);
				}
			}
			catch (Exception ex)
			{
				MarkStorageError(ex);
				api.Logger.Warning("[BlockLogger/Storage] Final SQLite maintenance failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}
			finally
			{
				if (flag2)
				{
					writeGate.Release();
				}
			}
		}
		if (!flag)
		{
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
