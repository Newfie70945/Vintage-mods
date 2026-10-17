namespace BlockLogger.WebDashboard;

public sealed class DatabaseHealthSnapshot
{
	public bool Available { get; set; }

	public bool Ready { get; set; }

	public string Path { get; set; } = "";

	public long DatabaseBytes { get; set; }

	public long WalBytes { get; set; }

	public long ShmBytes { get; set; }

	public long PendingEvents { get; set; }

	public long QueueCapacity { get; set; }

	public long PendingOverflowEvents { get; set; }

	public long PeakOverflowEvents { get; set; }

	public long OverflowCapacity { get; set; }

	public long OverflowAcceptedEvents { get; set; }

	public long OverflowRejectedEvents { get; set; }

	public long DroppedEvents { get; set; }

	public long LastBatchRows { get; set; }

	public long LastBatchMilliseconds { get; set; }

	public long BatchCount { get; set; }

	public double AverageBatchMilliseconds { get; set; }

	public double P95BatchMilliseconds { get; set; }

	public long MaxBatchMilliseconds { get; set; }

	public double RowsPerMinute { get; set; }

	public int QueryQueueDepth { get; set; }

	public int SnapshotQueryQueueDepth { get; set; }

	public long QueryCount { get; set; }

	public double AverageQueryMilliseconds { get; set; }

	public double P95QueryMilliseconds { get; set; }

	public long MaxQueryMilliseconds { get; set; }

	public long LastQueryMilliseconds { get; set; }

	public string LastQueryKind { get; set; } = "";

	public long SlowQueryCount { get; set; }

	public bool WriteGateBusy { get; set; }

	public long BusyOrLockedErrors { get; set; }

	public long LastIntegrityCheckUnixMilliseconds { get; set; }

	public long LastIntegrityCheckMilliseconds { get; set; }

	public long LastBackupUnixMilliseconds { get; set; }

	public int BackupCount { get; set; }

	public long BackupBytes { get; set; }

	public int BackupRetentionCount { get; set; }

	public int AutoBackupIntervalHours { get; set; }

	public bool BackupInProgress { get; set; }

	public long LastSuccessfulWriteUnixMilliseconds { get; set; }

	public string LastError { get; set; } = "";

	public long LastErrorUnixMilliseconds { get; set; }

	public int RetentionDays { get; set; }

	public string Integrity { get; set; } = "Not run";

	public string Message { get; set; } = "";
}
