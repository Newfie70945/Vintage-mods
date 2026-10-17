namespace BlockLogger.Analytics;

public sealed class PlayerCountDailyPoint
{
	public string Date { get; set; } = "";

	public int Low { get; set; }

	public int High { get; set; }

	public double Average { get; set; }

	public double ObservedMinutes { get; set; }
}
