namespace BlockLogger.ServerHealth;

public sealed class ServerAdaptiveBaseline
{
	public int SampleCount { get; set; }

	public double DelayMedianMs { get; set; }

	public double DelayMadMs { get; set; }

	public double DelayAnomalyMs { get; set; }

	public double CpuMedianPercent { get; set; }

	public double CpuMadPercent { get; set; }

	public double CpuAnomalyPercent { get; set; }

	public double HeapMedianMb { get; set; }

	public double HeapMadMb { get; set; }

	public double EntityMedian { get; set; }

	public double EntityMad { get; set; }

	public double QueueMedian { get; set; }

	public double QueueMad { get; set; }

	public double QueueAnomaly { get; set; }

	public double WriterMedianMs { get; set; }

	public double WriterMadMs { get; set; }

	public double WriterAnomalyMs { get; set; }
}
