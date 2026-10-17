using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class PerformanceEnvironmentSnapshot
{
	public string Fingerprint { get; set; } = "";

	public long CapturedUnixMilliseconds { get; set; }

	public List<PerformanceModIdentity> Mods { get; set; } = new List<PerformanceModIdentity>();

	public PerformanceEnvironmentMetrics Metrics { get; set; } = new PerformanceEnvironmentMetrics();
}
