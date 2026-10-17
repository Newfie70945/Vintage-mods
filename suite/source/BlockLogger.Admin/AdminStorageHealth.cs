namespace BlockLogger.Admin;

public sealed class AdminStorageHealth
{
	public bool Available { get; set; }

	public string Path { get; set; } = "";

	public long DatabaseBytes { get; set; }

	public long WalBytes { get; set; }

	public long ShmBytes { get; set; }

	public long SaveCount { get; set; }

	public long LastSaveUnixMilliseconds { get; set; }

	public long LastSaveMilliseconds { get; set; }

	public long LastBackupUnixMilliseconds { get; set; }

	public int BackupCount { get; set; }

	public long LastIntegrityUnixMilliseconds { get; set; }

	public long LastIntegrityMilliseconds { get; set; }

	public string Integrity { get; set; } = "Not run";

	public string LastError { get; set; } = "";

	public long LastErrorUnixMilliseconds { get; set; }

	public int Players { get; set; }

	public int AuditRows { get; set; }

	public int InventorySnapshots { get; set; }

	public bool Dirty { get; set; }
}
