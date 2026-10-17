namespace BlockLogger.WebDashboard;

public sealed class PreflightCheck
{
	public string Severity { get; set; } = "Info";

	public string Category { get; set; } = "";

	public string Title { get; set; } = "";

	public string Detail { get; set; } = "";

	public string RecommendedAction { get; set; } = "";
}
