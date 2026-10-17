namespace BlockLogger.Analytics;

public sealed class PlayerCountHourlyAccumulator : PlayerCountAccumulator
{
	public string Date { get; set; } = "";

	public int Hour { get; set; }
}
