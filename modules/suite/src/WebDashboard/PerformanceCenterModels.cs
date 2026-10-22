using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class PerformanceEntityScanSnapshot
{
    public long UnixMilliseconds { get; set; }
    public int TotalLoadedEntities { get; set; }
    public int PositionedEntities { get; set; }
    public int UnpositionedEntities { get; set; }
    public List<PerformanceEntityTypeHotspot> EntityTypes { get; set; } = new();
    public List<PerformanceChunkHotspot> Chunks { get; set; } = new();
}

public sealed class PerformanceEntityTypeHotspot
{
    public string Code { get; set; } = "";
    public string RuntimeType { get; set; } = "";
    public int Count { get; set; }
    public int ChunkCount { get; set; }
}

public sealed class PerformanceChunkHotspot
{
    public int ChunkX { get; set; }
    public int ChunkZ { get; set; }
    public int Dimension { get; set; }
    public int EntityCount { get; set; }
    public int PlayerCount { get; set; }
    public int UniqueEntityTypes { get; set; }
    public string TopEntityTypes { get; set; } = "";
    public int PressureScore { get; set; }
}

public sealed class PerformanceBlockActivityHotspot
{
    public string Dimension { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public string BlockCode { get; set; } = "";
    public int EventCount { get; set; }
    public int DistinctActions { get; set; }
    public long LastTimestampTicks { get; set; }
}


public sealed class PerformanceCenterStore
{
    public int SchemaVersion { get; set; } = 2;
    public List<PerformanceIncidentArchive> Incidents { get; set; } = new();
    public List<PerformanceEntityScanSnapshot> EntityScans { get; set; } = new();
    public PerformanceEnvironmentSnapshot? CurrentEnvironment { get; set; }
    public List<PerformanceEnvironmentChange> EnvironmentHistory { get; set; } = new();
}

public sealed class PerformanceIncidentArchive
{
    public string AssessmentStatus { get; set; } = "Insufficient evidence";
    public List<string> EvidenceGaps { get; set; } = new();
    public int MaxEntityDelta { get; set; }
    public int MaxChunkDelta { get; set; }
    public long BlockLoggerDropped { get; set; }
    public int AttributionVersion { get; set; }
    public string OriginalPrimaryCategory { get; set; } = "";
    public int Gen2Collections { get; set; }
    public bool GcPauseTimingAvailable { get; set; }
    public double GcPauseMs { get; set; }
    public double WorldSaveWindowMs { get; set; }
    public BlockLogger.ServerHealth.IncidentProfilerEvidence ProfileEvidence { get; set; } = new();
    public string Id { get; set; } = "";
    public long StartUnixMilliseconds { get; set; }
    public long EndUnixMilliseconds { get; set; }
    public long CapturedUnixMilliseconds { get; set; }
    public double DurationSeconds { get; set; }
    public string Severity { get; set; } = "";
    public string PrimaryCategory { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string NextAction { get; set; } = "";
    public string LikelyModId { get; set; } = "";
    public string LikelyModName { get; set; } = "";
    public string ModConfidence { get; set; } = "Low";
    public string ModEvidence { get; set; } = "";
    public string ModAttributionSource { get; set; } = "";
    public long ModProfileCompletedUnixMilliseconds { get; set; }
    public double WorstDelayMs { get; set; }
    public double MaxCpuPercent { get; set; }
    public List<BlockLogger.ServerHealth.ServerHealthSample> Before { get; set; } = new();
    public List<BlockLogger.ServerHealth.ServerHealthSample> During { get; set; } = new();
    public List<BlockLogger.ServerHealth.ServerHealthSample> After { get; set; } = new();
    public DatabaseHealthSnapshot Database { get; set; } = new();
    public WebServerDiagnosticsSnapshot WebServer { get; set; } = new();
    public List<WebAuditRecord> RecentAdminActions { get; set; } = new();
    public List<IncidentCauseScore> CauseScores { get; set; } = new();
}

public sealed class PerformanceEntityGrowthRow
{
    public string Code { get; set; } = "";
    public int Before { get; set; }
    public int After { get; set; }
    public int Delta { get; set; }
    public double PercentChange { get; set; }
    public string Severity { get; set; } = "Info";
    public string Reason { get; set; } = "";
}

public sealed class PerformanceEnvironmentSnapshot
{
    public string Fingerprint { get; set; } = "";
    public long CapturedUnixMilliseconds { get; set; }
    public List<PerformanceModIdentity> Mods { get; set; } = new();
    public PerformanceEnvironmentMetrics Metrics { get; set; } = new();
}

public sealed class PerformanceModIdentity
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class PerformanceEnvironmentMetrics
{
    public int SampleCount { get; set; }
    public int HealthScore { get; set; }
    public double DelayMedianMs { get; set; }
    public double CpuMedianPercent { get; set; }
    public double HeapMedianMb { get; set; }
    public double EntityMedian { get; set; }
    public double QueueMedian { get; set; }
    public double WriterMedianMs { get; set; }
    public double P95ProfileTickMs { get; set; }
}

public sealed class PerformanceEnvironmentChange
{
    public long DetectedUnixMilliseconds { get; set; }
    public string BeforeFingerprint { get; set; } = "";
    public string AfterFingerprint { get; set; } = "";
    public List<string> ChangedMods { get; set; } = new();
    public PerformanceEnvironmentMetrics Before { get; set; } = new();
    public PerformanceEnvironmentMetrics After { get; set; } = new();
    public string Result { get; set; } = "Collecting";
    public string Confidence { get; set; } = "Low";
    public double RegressionScore { get; set; }
    public string Reason { get; set; } = "";
}
