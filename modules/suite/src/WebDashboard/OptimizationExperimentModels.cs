using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class OptimizationExperimentStore
{
    public int SchemaVersion { get; set; } = 2;
    public OptimizationExperiment? Pending { get; set; }
    public List<OptimizationExperiment> History { get; set; } = new();
}

public sealed class OptimizationExperiment
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "tungsten";
    public string ProviderName { get; set; } = "Tungsten";
    public string OptimizationCode { get; set; } = "";
    public string OptimizationName { get; set; } = "";
    public bool TargetEnabled { get; set; }
    public long CreatedUnixMilliseconds { get; set; }
    public string RuntimeId { get; set; } = "";
    public string Status { get; set; } = "AwaitingRestart";
    public OptimizationMetricSnapshot Baseline { get; set; } = new();
    public OptimizationMetricSnapshot? After { get; set; }
    public string Result { get; set; } = "";
    public string Confidence { get; set; } = "Low";
    public double ImprovementScore { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class OptimizationMetricSnapshot
{
    public long CapturedUnixMilliseconds { get; set; }
    public int SampleCount { get; set; }
    public int Players { get; set; }
    public int HealthScore { get; set; }
    public string SmartState { get; set; } = "";
    public double DelayMedianMs { get; set; }
    public double CpuMedianPercent { get; set; }
    public double HeapMedianMb { get; set; }
    public double EntityMedian { get; set; }
    public double QueueMedian { get; set; }
    public double WriterMedianMs { get; set; }
    public double WorstDelayMs { get; set; }
    public int Gen2Collections { get; set; }
}
