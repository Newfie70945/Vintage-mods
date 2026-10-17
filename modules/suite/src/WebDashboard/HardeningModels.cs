using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class ServerUtilitiesSelfTest
{
    public string BuildId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string TargetGameVersion { get; set; } = "1.22.7";
    public long UnixMilliseconds { get; set; }
    public string Overall { get; set; } = "Unknown";
    public List<SelfTestCheck> Checks { get; set; } = new();
    public List<FeatureCircuitBreakerStatus> CircuitBreakers { get; set; } = new();
    public List<FeatureOverheadSnapshot> Overhead { get; set; } = new();
    public List<WebRoutePolicyInfo> WebPolicies { get; set; } = new();
}

public sealed class SelfTestCheck
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public string Detail { get; set; } = "";
    public string Safety { get; set; } = "Safe";
}

public sealed class FeatureCircuitBreakerStatus
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int ConsecutiveFailures { get; set; }
    public long TotalFailures { get; set; }
    public bool Open { get; set; }
    public long OpenUntilUnixMilliseconds { get; set; }
    public string LastError { get; set; } = "";
    public long LastFailureUnixMilliseconds { get; set; }
    public string Safety { get; set; } = "Safe";
}

public sealed class FeatureOverheadSnapshot
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public long Calls { get; set; }
    public double LastMilliseconds { get; set; }
    public double AverageMilliseconds { get; set; }
    public double MaxMilliseconds { get; set; }
}

public sealed class WebRoutePolicyInfo
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Access { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Safety { get; set; } = "";
    public int CooldownSeconds { get; set; }
    public bool ExclusiveHeavyOperation { get; set; }
}

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
