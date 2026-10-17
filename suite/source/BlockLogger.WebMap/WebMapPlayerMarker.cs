namespace BlockLogger.WebMap;

public sealed class WebMapPlayerMarker
{
	public string Name { get; set; } = "";

	public string Uid { get; set; } = "";

	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int Dimension { get; set; }

	public int PingMilliseconds { get; set; }

	public long TotalPlaySeconds { get; set; }

	public long SessionSeconds { get; set; }

	public float YawRadians { get; set; }

	public WebMapPlayerMarker Clone()
	{
		return (WebMapPlayerMarker)MemberwiseClone();
	}
}
