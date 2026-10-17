namespace BlockLogger.WebDashboard;

public sealed class PlayerLoadBucket
{
	public string Label { get; set; } = "";

	public int MinimumPlayers { get; set; }

	public int MaximumPlayers { get; set; }

	public long Samples { get; set; }

	public double DelayTotalMs { get; set; }

	public double CpuTotalPercent { get; set; }

	public double DelayMaxMs { get; set; }

	public double CpuMaxPercent { get; set; }

	public long StallSamples { get; set; }

	public long ExcludedLifecycleSamples { get; set; }

	public double ExcludedLifecycleMaxDelayMs { get; set; }

	public bool LegacyLifecycleCleanupApplied { get; set; }
}
