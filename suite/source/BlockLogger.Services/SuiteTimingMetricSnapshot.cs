namespace BlockLogger.Services;

public sealed class SuiteTimingMetricSnapshot
{
	public string Name { get; set; } = "";

	public int Samples { get; set; }

	public long TotalCount { get; set; }

	public double LastMilliseconds { get; set; }

	public double AverageMilliseconds { get; set; }

	public double P95Milliseconds { get; set; }

	public double MaxMilliseconds { get; set; }

	public long CallsPerMinute { get; set; }

	public double TimePerMinuteMilliseconds { get; set; }
}
