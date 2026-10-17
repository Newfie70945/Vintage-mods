namespace BlockLogger.WebMap;

public sealed class WebMapLeaderboardEntry
{
	public int Rank { get; set; }

	public string Player { get; set; } = "";

	public bool Online { get; set; }

	public double Value { get; set; }

	public string DisplayValue { get; set; } = "";

	public int Level { get; set; }

	public double Experience { get; set; }
}
