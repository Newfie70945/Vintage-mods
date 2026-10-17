namespace BlockLogger.Admin;

public sealed class AdminAuditRecord
{
	public long Id { get; set; }

	public long UnixMilliseconds { get; set; }

	public string Actor { get; set; } = "";

	public string Action { get; set; } = "";

	public string TargetUid { get; set; } = "";

	public string TargetName { get; set; } = "";

	public string Detail { get; set; } = "";

	public string Source { get; set; } = "ingame";
}
