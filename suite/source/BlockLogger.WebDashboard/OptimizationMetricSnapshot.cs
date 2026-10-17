namespace BlockLogger.WebDashboard;

public sealed class OptimizationMetricSnapshot
{
	public long CapturedUnixMilliseconds { get; set; }

	public int SampleCount { get; set; }

	public int Players { get; set; }

	public int HealthScore { get; set; }

	public string SmartState { get; set; } = "";

	public double DelayMedianMs { get; set; }

	public double CpuMedianPercent { get; set; }

	public double HeapMedianMb { get; set; }

	public double EntityMedian { get; set; }

	public double QueueMedian { get; set; }

	public double WriterMedianMs { get; set; }

	public double WorstDelayMs { get; set; }

	public int Gen2Collections { get; set; }
}
