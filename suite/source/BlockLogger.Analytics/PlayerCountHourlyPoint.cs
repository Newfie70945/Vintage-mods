namespace BlockLogger.Analytics;

public sealed class PlayerCountHourlyPoint
{
	public string Date { get; set; } = "";

	public int Hour { get; set; }

	public string Label { get; set; } = "";

	public double? Average { get; set; }

	public int? Low { get; set; }

	public int? High { get; set; }

	public double ObservedMinutes { get; set; }
}
