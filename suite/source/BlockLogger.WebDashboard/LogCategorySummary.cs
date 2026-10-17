namespace BlockLogger.WebDashboard;

public sealed class LogCategorySummary
{
	public string Category { get; set; } = "";

	public int Occurrences { get; set; }

	public int UniqueIssues { get; set; }

	public string Impact { get; set; } = "Low";
}
