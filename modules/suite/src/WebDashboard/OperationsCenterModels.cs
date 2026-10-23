using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class OperationsCenterStore
{
    public int SchemaVersion { get; set; } = 2;
    public List<ServerAlertRecord> Alerts { get; set; } = new();
    public List<ServerActionRecord> Actions { get; set; } = new();
    public List<WebAuditRecord> Audit { get; set; } = new();
    public List<PlayerLoadBucket> PlayerLoad { get; set; } = new();
    public List<ConfigSnapshotRecord> ConfigSnapshots { get; set; } = new();
    public List<WorldSaveObservation> WorldSaves { get; set; } = new();
    public List<NetworkPerformanceSample> NetworkSamples { get; set; } = new();
    public List<PerformanceSnapshotRecord> PerformanceSnapshots { get; set; } = new();
    public List<MemoryInspectorSnapshot> MemoryInspectorSnapshots { get; set; } = new();
}

public sealed class ServerAlertRecord
{
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public string Severity { get; set; } = "Watch";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
    public string Status { get; set; } = "Active";
    public int Occurrences { get; set; } = 1;
}

public sealed class ServerActionRecord
{
    public string Id { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public long UpdatedUnixMilliseconds { get; set; }
    public string Priority { get; set; } = "Medium";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string TargetSection { get; set; } = "performance";
    public string Status { get; set; } = "Open";
}

public sealed class WebAuditRecord
{
    public long UnixMilliseconds { get; set; }
    public string Actor { get; set; } = "";
    public string Remote { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Result { get; set; } = "";
}

public sealed class PlayerLoadBucket
{
    public string Label { get; set; } = "";
    public int MinimumPlayers { get; set; }
    public int MaximumPlayers { get; set; }
    public long Samples { get; set; }
    public double DelayTotalMs { get; set; }
    public double CpuTotalPercent { get; set; }
    public double DelayMaxMs { get; set; }
    public double CpuMaxPercent { get; set; }
    public long StallSamples { get; set; }

    // Player-load modelling intentionally excludes lifecycle/sampler discontinuities.
    // Keep counters so the dashboard can disclose that filtering occurred without
    // letting a startup pause distort the workload model for months.
    public long ExcludedLifecycleSamples { get; set; }
    public double ExcludedLifecycleMaxDelayMs { get; set; }
    public bool LegacyLifecycleCleanupApplied { get; set; }
}

public sealed class ConfigSnapshotRecord
{
    public string Id { get; set; } = "";
    public long UnixMilliseconds { get; set; }
    public string Label { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string SuiteFeaturesJson { get; set; } = "";
    public string BlockLoggerJson { get; set; } = "";
    public string WebDashboardJson { get; set; } = "";
    public string AnimalLimiterJson { get; set; } = "";
    public string LoginProtectionJson { get; set; } = "";
    public string PublicPlayerListJson { get; set; } = "";
}


public sealed class MemoryInspectorTrackedStructure
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string RuntimeType { get; set; } = "";
    public long Count { get; set; }
    public string Note { get; set; } = "";
}

public sealed class MemoryInspectorSnapshot
{
    public string Id { get; set; } = "";
    public long UnixMilliseconds { get; set; }
    public long ProcessStartedUnixMilliseconds { get; set; }
    public string Label { get; set; } = "";
    public double ManagedHeapMb { get; set; }
    public double WorkingSetMb { get; set; }
    public double PrivateMemoryMb { get; set; }
    public double GcCommittedMb { get; set; }
    public double GcFragmentedMb { get; set; }
    public double EstimatedNativeMb { get; set; }
    public double TotalAllocatedMb { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int Players { get; set; }
    public int LoadedEntities { get; set; }
    public int LoadedChunks { get; set; }
    public List<MemoryInspectorTrackedStructure> Structures { get; set; } = new();
}

public sealed class MemoryGcAnalysis
{
    public bool Available { get; set; }
    public int SampleCount { get; set; }
    public string State { get; set; } = "Unavailable";
    public int Score { get; set; }
    public double HeapCurrentMb { get; set; }
    public double HeapMinimumMb { get; set; }
    public double HeapMaximumMb { get; set; }
    public double HeapSlopeMbPerMinute { get; set; }
    public double PrivateCurrentMb { get; set; }
    public double PrivateSlopeMbPerMinute { get; set; }
    public double EstimatedNativeCurrentMb { get; set; }
    public double EstimatedNativeSlopeMbPerMinute { get; set; }
    public double GcCommittedCurrentMb { get; set; }
    public double GcFragmentedCurrentMb { get; set; }
    public int Gen0Increase { get; set; }
    public int Gen1Increase { get; set; }
    public int Gen2Increase { get; set; }
    public double EstimatedPostGcFloorStartMb { get; set; }
    public double EstimatedPostGcFloorEndMb { get; set; }
    public double EstimatedPostGcFloorGrowthMb { get; set; }
    public string Finding { get; set; } = "";
    public string Recommendation { get; set; } = "";
}


public sealed class WorldSaveObservation
{
    public long UnixMilliseconds { get; set; }
    public double ObservedMainThreadWindowMs { get; set; }
    public int Players { get; set; }
    public double DelayAtStartMs { get; set; }
    public string Severity { get; set; } = "Info";
    public string Note { get; set; } = "";
}

public sealed class NetworkPerformanceSample
{
    public long UnixMilliseconds { get; set; }
    public long TotalSentBytes { get; set; }
    public long TotalReceivedBytes { get; set; }
    public double SentBytesPerSecond { get; set; }
    public double ReceivedBytesPerSecond { get; set; }
    public int Players { get; set; }
}


public sealed class PerformanceScorecardRow
{
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public string State { get; set; } = "Healthy";
    public string Reason { get; set; } = "";
    public string TargetSection { get; set; } = "performance";
}


public sealed class PerformanceSnapshotRecord
{
    public string Id { get; set; } = "";
    public long UnixMilliseconds { get; set; }
    public string Label { get; set; } = "";
    public int Players { get; set; }
    public string ModFingerprint { get; set; } = "";
    public PerformanceEnvironmentMetrics Metrics { get; set; } = new();
}


// Main-thread-only workflow operation. IDs come from the reviewed browser snapshot,
// so records arriving during a request are never accidentally cleared.
public static class ActionCenterWorkflow
{
    public static int Clear(OperationsCenterStore store, string target, System.Collections.Generic.IReadOnlyCollection<string> ids)
    {
        var selected = new System.Collections.Generic.HashSet<string>(ids, System.StringComparer.Ordinal);
        if (target == "alerts")
        {
            var removed = store.Alerts.FindAll(x => selected.Contains(x.Id));
            store.Alerts.RemoveAll(x => selected.Contains(x.Id));
            var linked = new System.Collections.Generic.HashSet<string>(removed.ConvertAll(x => "alert:" + x.Id), System.StringComparer.Ordinal);
            store.Actions.RemoveAll(x => linked.Contains(x.Id));
            return removed.Count;
        }
        if (target != "actions") throw new System.ArgumentException("Unknown clear target.");
        int count = 0;
        foreach (var action in store.Actions)
        {
            if (action.Status == "Cleared" || !selected.Contains(action.Id)) continue;
            action.Status = "Cleared";
            count++;
        }
        return count;
    }
}
