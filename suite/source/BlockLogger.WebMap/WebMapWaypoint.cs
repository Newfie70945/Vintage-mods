namespace BlockLogger.WebMap;

public sealed class WebMapWaypoint
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Description { get; set; } = "";

	public string Type { get; set; } = "major";

	public string Icon { get; set; } = "pin";

	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int Dimension { get; set; }

	public string Color { get; set; } = "#38bdf8";

	public bool IsPlayerWaypoint { get; set; }

	public string OwnerPlayerUid { get; set; } = "";

	public string OwnerPlayerName { get; set; } = "";

	public string CreatedBy { get; set; } = "";

	public long CreatedUnixMilliseconds { get; set; }

	public string UpdatedBy { get; set; } = "";

	public long UpdatedUnixMilliseconds { get; set; }

	public WebMapWaypoint Clone()
	{
		return (WebMapWaypoint)MemberwiseClone();
	}
}
