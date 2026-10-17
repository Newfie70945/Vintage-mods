namespace BlockLogger.ServerHealth;

public sealed class ServerDiagnosticSummary
{
	public string State { get; set; } = "Disabled";

	public int WindowMinutes { get; set; } = 5;

	public string Summary { get; set; } = "";

	public int StallCount { get; set; }

	public int SevereStallCount { get; set; }

	public double WorstDelayMs { get; set; }

	public double AverageCpuPercent { get; set; }

	public double MaxCpuPercent { get; set; }

	public int Gen2Collections { get; set; }

	public int MaxBlockLoggerQueue { get; set; }

	public long MaxBlockLoggerWriterMs { get; set; }

	public long BlockLoggerDroppedIncrease { get; set; }

	public int MaxLoadedEntities { get; set; }

	public int MaxLoadedChunks { get; set; }
}
