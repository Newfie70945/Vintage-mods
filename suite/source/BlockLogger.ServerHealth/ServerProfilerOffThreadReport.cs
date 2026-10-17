namespace BlockLogger.ServerHealth;

public sealed class ServerProfilerOffThreadReport
{
	public double DurationMs { get; set; }

	public string Summary { get; set; } = "";

	public string ModId { get; set; } = "";

	public string Confidence { get; set; } = "Low";
}
