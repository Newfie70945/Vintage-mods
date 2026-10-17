namespace BlockLogger.WebDashboard;

public sealed class WebAuditRecord
{
	public long UnixMilliseconds { get; set; }

	public string Actor { get; set; } = "";

	public string Remote { get; set; } = "";

	public string Action { get; set; } = "";

	public string Detail { get; set; } = "";

	public string Result { get; set; } = "";
}
