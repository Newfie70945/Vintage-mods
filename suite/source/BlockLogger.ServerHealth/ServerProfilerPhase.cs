namespace BlockLogger.ServerHealth;

public sealed class ServerProfilerPhase
{
	public string Code { get; set; } = "";

	public double TotalMs { get; set; }

	public double AveragePerTickMs { get; set; }

	public double MaxSingleMs { get; set; }

	public long Calls { get; set; }

	public double PercentOfProfileTickTime { get; set; }

	public string ModId { get; set; } = "";

	public string AttributionConfidence { get; set; } = "Low";
}
