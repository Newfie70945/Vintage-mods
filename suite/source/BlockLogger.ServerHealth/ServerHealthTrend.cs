namespace BlockLogger.ServerHealth;

public sealed class ServerHealthTrend
{
	public string Name { get; set; } = "";

	public string Category { get; set; } = "";

	public string Unit { get; set; } = "";

	public double SlopePerMinute { get; set; }

	public double NetChange { get; set; }

	public string Severity { get; set; } = "Info";

	public string Confidence { get; set; } = "Low";

	public string Interpretation { get; set; } = "";
}
