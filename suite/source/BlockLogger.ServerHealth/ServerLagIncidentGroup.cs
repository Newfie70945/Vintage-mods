namespace BlockLogger.ServerHealth;

public sealed class ServerLagIncidentGroup
{
	public long StartUnixMilliseconds { get; set; }

	public long EndUnixMilliseconds { get; set; }

	public double DurationSeconds { get; set; }

	public int SampleCount { get; set; }

	public string Severity { get; set; } = "Watch";

	public string PrimaryCategory { get; set; } = "";

	public string Confidence { get; set; } = "Low";

	public double WorstDelayMs { get; set; }

	public double MaxCpuPercent { get; set; }

	public int Gen2Collections { get; set; }

	public int MaxBlockLoggerQueue { get; set; }

	public long MaxBlockLoggerWriterMs { get; set; }

	public long BlockLoggerDropped { get; set; }

	public int MaxEntityDelta { get; set; }

	public int MaxChunkDelta { get; set; }

	public string Evidence { get; set; } = "";

	public string NextAction { get; set; } = "";

	public string LikelyModId { get; set; } = "";

	public string LikelyModName { get; set; } = "";

	public string ModConfidence { get; set; } = "Low";

	public string ModEvidence { get; set; } = "";

	public string ModAttributionSource { get; set; } = "";

	public long ModProfileCompletedUnixMilliseconds { get; set; }
}
