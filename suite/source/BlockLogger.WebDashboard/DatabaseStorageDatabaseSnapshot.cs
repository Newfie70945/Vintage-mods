using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class DatabaseStorageDatabaseSnapshot
{
	public string Key { get; set; } = "";

	public string Name { get; set; } = "";

	public string FileName { get; set; } = "";

	public bool Available { get; set; }

	public bool Detailed { get; set; }

	public bool PayloadEstimates { get; set; }

	public bool DbStatAvailable { get; set; }

	public string AnalysisMessage { get; set; } = "";

	public long DatabaseBytes { get; set; }

	public long WalBytes { get; set; }

	public long ShmBytes { get; set; }

	public long BackupBytes { get; set; }

	public int BackupCount { get; set; }

	public long PageSizeBytes { get; set; }

	public long PageCount { get; set; }

	public long LogicalDatabaseBytes { get; set; }

	public long FreePageCount { get; set; }

	public long FreeBytes { get; set; }

	public long TableDataBytes { get; set; }

	public long IndexBytes { get; set; }

	public long SQLiteOverheadBytes { get; set; }

	public int RetentionDays { get; set; }

	public List<DatabaseStorageContentSnapshot> Contents { get; set; } = new List<DatabaseStorageContentSnapshot>();

	public List<DatabaseStorageTableSnapshot> Tables { get; set; } = new List<DatabaseStorageTableSnapshot>();

	public List<DatabaseStorageRecommendation> Recommendations { get; set; } = new List<DatabaseStorageRecommendation>();
}
