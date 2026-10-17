using System.Collections.Generic;

namespace BlockLogger.ServerHealth;

public sealed class ServerSmartAnalysis
{
	public bool Available { get; set; }

	public string State { get; set; } = "Disabled";

	public int HealthScore { get; set; }

	public string PrimaryFinding { get; set; } = "";

	public string PrimaryConfidence { get; set; } = "Low";

	public string AnalysisConfidence { get; set; } = "Low";

	public int DataQualityScore { get; set; }

	public string RecoveryState { get; set; } = "Unknown";

	public ServerAdaptiveBaseline Baseline { get; set; } = new ServerAdaptiveBaseline();

	public List<ServerSmartSignal> Signals { get; set; } = new List<ServerSmartSignal>();

	public List<ServerHealthTrend> Trends { get; set; } = new List<ServerHealthTrend>();

	public List<ServerLagIncidentGroup> IncidentGroups { get; set; } = new List<ServerLagIncidentGroup>();

	public List<string> Recommendations { get; set; } = new List<string>();

	public double RecentDelayMedianMs { get; set; }

	public double RecentCpuMedianPercent { get; set; }

	public double RecentHeapMedianMb { get; set; }

	public double RecentEntityMedian { get; set; }

	public double RecentQueueMedian { get; set; }

	public double RecentWriterMedianMs { get; set; }
}
