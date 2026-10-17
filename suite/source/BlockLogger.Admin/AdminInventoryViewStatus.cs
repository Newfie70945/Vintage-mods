namespace BlockLogger.Admin;

public sealed class AdminInventoryViewStatus
{
	public bool Available { get; set; }

	public bool ReadOnly { get; set; } = true;

	public string Source { get; set; } = "unavailable";

	public long CapturedUnixMilliseconds { get; set; }

	public long ReadUnixMilliseconds { get; set; }

	public long SnapshotId { get; set; }

	public bool Partial { get; set; }

	public string Message { get; set; } = "No saved inventory is available for this player.";
}
