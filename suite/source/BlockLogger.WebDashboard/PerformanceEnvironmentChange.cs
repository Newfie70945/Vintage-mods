using System.Collections.Generic;

namespace BlockLogger.WebDashboard;

public sealed class PerformanceEnvironmentChange
{
	public long DetectedUnixMilliseconds { get; set; }

	public string BeforeFingerprint { get; set; } = "";

	public string AfterFingerprint { get; set; } = "";

	public List<string> ChangedMods { get; set; } = new List<string>();

	public PerformanceEnvironmentMetrics Before { get; set; } = new PerformanceEnvironmentMetrics();

	public PerformanceEnvironmentMetrics After { get; set; } = new PerformanceEnvironmentMetrics();

	public string Result { get; set; } = "Collecting";

	public string Confidence { get; set; } = "Low";

	public double RegressionScore { get; set; }

	public string Reason { get; set; } = "";
}
