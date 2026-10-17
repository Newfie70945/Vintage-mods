namespace BlockLogger.WebDashboard;

public sealed class ServerActionRecord
{
	public string Id { get; set; } = "";

	public string SourceKey { get; set; } = "";

	public long UpdatedUnixMilliseconds { get; set; }

	public string Priority { get; set; } = "Medium";

	public string Category { get; set; } = "";

	public string Title { get; set; } = "";

	public string Reason { get; set; } = "";

	public string TargetSection { get; set; } = "performance";

	public string Status { get; set; } = "Open";
}
