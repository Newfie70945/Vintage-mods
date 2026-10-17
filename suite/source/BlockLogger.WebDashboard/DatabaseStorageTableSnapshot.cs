namespace BlockLogger.WebDashboard;

public sealed class DatabaseStorageTableSnapshot
{
	public bool SizeEstimated { get; set; }

	public bool RowCountKnown { get; set; }

	public int SampledRows { get; set; }

	public double AveragePayloadBytes { get; set; }

	public long EstimatedPayloadBytes { get; set; }

	public string Name { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string Subsystem { get; set; } = "";

	public string StoredFields { get; set; } = "";

	public long ApproxRows { get; set; }

	public long DataBytes { get; set; }

	public long IndexBytes { get; set; }

	public long TotalBytes { get; set; }

	public double PercentOfDatabase { get; set; }

	public string Description { get; set; } = "";

	public string GrowthDriver { get; set; } = "";

	public string Improvement { get; set; } = "";

	public string Safety { get; set; } = "Safe";
}
