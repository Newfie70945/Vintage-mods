namespace BlockLogger.WebDashboard;

public sealed class PerformanceSnapshotRecord
{
	public string Id { get; set; } = "";

	public long UnixMilliseconds { get; set; }

	public string Label { get; set; } = "";

	public int Players { get; set; }

	public string ModFingerprint { get; set; } = "";

	public PerformanceEnvironmentMetrics Metrics { get; set; } = new PerformanceEnvironmentMetrics();
}
