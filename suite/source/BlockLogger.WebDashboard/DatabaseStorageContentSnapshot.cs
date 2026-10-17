namespace BlockLogger.WebDashboard;

public sealed class DatabaseStorageContentSnapshot
{
	public string Subsystem { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string TableName { get; set; } = "";

	public string Description { get; set; } = "";

	public string StoredFields { get; set; } = "";

	public bool Present { get; set; }

	public bool Analyzed { get; set; }

	public long ApproxRows { get; set; }

	public long DataBytes { get; set; }

	public long IndexBytes { get; set; }

	public long TotalBytes { get; set; }
}
