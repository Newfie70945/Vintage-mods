namespace BlockLogger.Reports;

public sealed class PlayerReportNote
{
	public long UnixMilliseconds { get; set; }

	public string Author { get; set; } = "";

	public string Text { get; set; } = "";
}
