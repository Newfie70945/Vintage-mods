namespace BlockLogger.WebDashboard;

public sealed class PerformanceBlockActivityHotspot
{
	public string Dimension { get; set; } = "";

	public int X { get; set; }

	public int Y { get; set; }

	public int Z { get; set; }

	public string BlockCode { get; set; } = "";

	public int EventCount { get; set; }

	public int DistinctActions { get; set; }

	public long LastTimestampTicks { get; set; }
}
