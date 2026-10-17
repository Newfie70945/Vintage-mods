namespace BlockLogger.WebDashboard;

public sealed class PerformanceScorecardRow
{
	public string Name { get; set; } = "";

	public int Score { get; set; }

	public string State { get; set; } = "Healthy";

	public string Reason { get; set; } = "";

	public string TargetSection { get; set; } = "performance";
}
