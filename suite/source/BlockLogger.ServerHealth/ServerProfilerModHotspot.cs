namespace BlockLogger.ServerHealth;

public sealed class ServerProfilerModHotspot
{
	public string ModId { get; set; } = "";

	public string ModName { get; set; } = "";

	public double TotalMs { get; set; }

	public double MaxSingleMs { get; set; }

	public long Calls { get; set; }

	public int SlowSegments { get; set; }

	public double PercentOfProfileTickTime { get; set; }

	public string Confidence { get; set; } = "Low";

	public string TopMarker { get; set; } = "";

	public bool IsCore { get; set; }
}
