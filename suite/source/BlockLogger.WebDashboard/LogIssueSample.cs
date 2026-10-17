namespace BlockLogger.WebDashboard;

public sealed class LogIssueSample
{
	public long UnixMilliseconds { get; set; }

	public string SourceFile { get; set; } = "";

	public bool Archived { get; set; }

	public string Context { get; set; } = "";
}
