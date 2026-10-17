namespace BlockLogger.WebDashboard;

public sealed class FeatureOverheadSnapshot
{
	public string Key { get; set; } = "";

	public string Name { get; set; } = "";

	public long Calls { get; set; }

	public double LastMilliseconds { get; set; }

	public double AverageMilliseconds { get; set; }

	public double MaxMilliseconds { get; set; }
}
