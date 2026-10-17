namespace BlockLogger.WebDashboard;

public sealed class ServerAlertRecord
{
	public string Id { get; set; } = "";

	public string Key { get; set; } = "";

	public long FirstSeenUnixMilliseconds { get; set; }

	public long LastSeenUnixMilliseconds { get; set; }

	public string Severity { get; set; } = "Watch";

	public string Category { get; set; } = "";

	public string Title { get; set; } = "";

	public string Evidence { get; set; } = "";

	public string RecommendedAction { get; set; } = "";

	public string Status { get; set; } = "Active";

	public int Occurrences { get; set; } = 1;
}
