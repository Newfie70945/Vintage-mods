namespace BlockLogger.Reports;

public sealed class PlayerReportEvent
{
	public long UnixMilliseconds { get; set; }

	public string Actor { get; set; } = "";

	public string Action { get; set; } = "";

	public string Detail { get; set; } = "";
}
