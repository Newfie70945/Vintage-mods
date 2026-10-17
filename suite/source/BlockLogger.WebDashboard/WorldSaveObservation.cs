namespace BlockLogger.WebDashboard;

public sealed class WorldSaveObservation
{
	public long UnixMilliseconds { get; set; }

	public double ObservedMainThreadWindowMs { get; set; }

	public int Players { get; set; }

	public double DelayAtStartMs { get; set; }

	public string Severity { get; set; } = "Info";

	public string Note { get; set; } = "";
}
