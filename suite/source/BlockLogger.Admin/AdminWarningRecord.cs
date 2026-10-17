namespace BlockLogger.Admin;

public sealed class AdminWarningRecord
{
	public long Id { get; set; }

	public long UnixMilliseconds { get; set; }

	public string IssuedBy { get; set; } = "";

	public string Reason { get; set; } = "";

	public bool Removed { get; set; }

	public string RemovedBy { get; set; } = "";

	public long RemovedUnixMilliseconds { get; set; }
}
