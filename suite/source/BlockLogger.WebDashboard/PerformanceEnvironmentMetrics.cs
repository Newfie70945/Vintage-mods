namespace BlockLogger.WebDashboard;

public sealed class PerformanceEnvironmentMetrics
{
	public int SampleCount { get; set; }

	public int HealthScore { get; set; }

	public double DelayMedianMs { get; set; }

	public double CpuMedianPercent { get; set; }

	public double HeapMedianMb { get; set; }

	public double EntityMedian { get; set; }

	public double QueueMedian { get; set; }

	public double WriterMedianMs { get; set; }

	public double P95ProfileTickMs { get; set; }
}
