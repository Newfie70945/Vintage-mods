namespace BlockLogger.WebDashboard;

public sealed class IncidentCauseScore
{
	public string Category { get; set; } = "";

	public int Score { get; set; }

	public string Confidence { get; set; } = "Low";

	public string Evidence { get; set; } = "";
}
