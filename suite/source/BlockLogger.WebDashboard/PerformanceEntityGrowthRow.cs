namespace BlockLogger.WebDashboard;

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
