namespace BlockLogger.Services;

public sealed class BlockLoggerPurgeResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public int KeepDays { get; set; }

	public int EventsRemoved { get; set; }

	public int SnapshotsRemoved { get; set; }

	public int EntityActivityRemoved { get; set; }

	public bool Complete { get; set; }

	public bool MoreRowsMayRemain { get; set; }

	public bool CompactRequested { get; set; }

	public bool Compacted { get; set; }

	public string CompactMessage { get; set; } = "";

	public long DatabaseBytesBefore { get; set; }

	public long DatabaseBytesAfter { get; set; }

	public long WalBytesBefore { get; set; }

	public long WalBytesAfter { get; set; }

	public long DurationMilliseconds { get; set; }

	public int TotalRemoved => EventsRemoved + SnapshotsRemoved + EntityActivityRemoved;
}
