namespace NewfiesOptimizer;

public sealed record MetricCapability(string Name, bool Available, string Reason);
public sealed record ModuleStatus(string Name, string Status, string Reason);
public sealed record TickSummary(long Count, double? AverageMs, double? MedianMs, double? P95Ms, double? P99Ms,
    double? WorstMs, long OverflowCount, double? EffectiveTps, double? MainThreadWorkPercent, string Source);
public sealed record HotspotSummary(string Name, string Category, long Calls, long SampledCalls, double TotalMs,
    double? AverageMs, double? P95Ms, double? WorstMs, string Scope);

public sealed record TelemetrySample
{
    public DateTimeOffset TimestampUtc { get; init; }
    public double ElapsedSeconds { get; init; }
    public long TickCount { get; init; }
    public double? EffectiveTps { get; init; }
    public double? AverageTickMs { get; init; }
    public double? P95TickMs { get; init; }
    public double? ProcessCpuPercent { get; init; }
    public double? MainThreadCpuPercent { get; init; }
    public long? ManagedHeapBytes { get; init; }
    public long? GcCommittedBytes { get; init; }
    public long? GcFragmentedBytes { get; init; }
    public long? WorkingSetBytes { get; init; }
    public long? PrivateMemoryBytes { get; init; }
    public long? AllocatedBytesDelta { get; init; }
    public int? Gc0Delta { get; init; }
    public int? Gc1Delta { get; init; }
    public int? Gc2Delta { get; init; }
    public double? GcPauseMsDelta { get; init; }
    public int? Players { get; init; }
    public int? LoadedEntities { get; init; }
    public int? LoadedChunks { get; init; }
    public int? GeneratingChunkColumns { get; init; }
    public long? NetworkSentBytesDelta { get; init; }
    public long? NetworkReceivedBytesDelta { get; init; }
    public double ProfilerOwnWorkMs { get; init; }
}

public sealed record ProfilerOverhead(long HookCalls, double HookWorkMs, long SampleCalls, double SampleWorkMs,
    string Caveat = "Self-timing excludes Harmony dispatch, exports and observer effects. Compare matching profiler-disabled/enabled trials to measure total overhead.");

public sealed record ProfileReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Kind { get; init; } = "newfies-optimizer-profile";
    public string OptimizerVersion { get; init; } = "0.7.0";
    public IReadOnlyDictionary<string,long> EngineCounters { get; init; } = new Dictionary<string,long>();
    public string GameVersion { get; init; } = "1.22.7";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset StoppedUtc { get; init; }
    public double DurationSeconds { get; init; }
    public bool IsRunning { get; init; }
    public required TickSummary Tick { get; init; }
    public required TelemetrySample[] Samples { get; init; }
    public required HotspotSummary[] Hotspots { get; init; }
    public required MetricCapability[] Capabilities { get; init; }
    public required ModuleStatus[] Modules { get; init; }
    public required ProfilerOverhead Overhead { get; init; }
    public long SamplesOverwritten { get; init; }
    public long HotspotLookupsOmitted { get; init; }
    public string[] Notes { get; init; } = [];
    public object Correctness { get; init; } = new { available = false, reason = "Collect authoritative interaction and workload evidence separately; these metrics do not prove correctness or player capacity." };
}
