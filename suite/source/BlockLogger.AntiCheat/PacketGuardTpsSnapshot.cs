using System;

namespace BlockLogger.AntiCheat;

internal readonly struct PacketGuardTpsSnapshot(bool enabled, bool ready, double targetTps, double estimatedTps, double healthPercent, double timingMultiplier, string band, int sampleCount, double smoothedTickMs, bool suppressTimingRejections)
{
	public bool Enabled { get; } = enabled;

	public bool Ready { get; } = ready;

	public double TargetTps { get; } = targetTps;

	public double EstimatedTps { get; } = estimatedTps;

	public double HealthPercent { get; } = healthPercent;

	public double TimingMultiplier { get; } = timingMultiplier;

	public string Band { get; } = band;

	public int SampleCount { get; } = sampleCount;

	public double SmoothedTickMs { get; } = smoothedTickMs;

	public bool SuppressTimingRejections { get; } = suppressTimingRejections;

	public bool IsCritical => string.Equals(Band, "Critical", StringComparison.OrdinalIgnoreCase);

	public bool IsDegraded => IsCritical || string.Equals(Band, "Degraded", StringComparison.OrdinalIgnoreCase);

	public string Evidence => $"tpsBand={Band};targetTps={TargetTps:0.0};estimatedTps={EstimatedTps:0.0};tpsHealthPct={HealthPercent:0.0};timingMultiplier={TimingMultiplier:0.00};tpsSamples={SampleCount};tpsReady={Ready};timingRejectSuppressed={SuppressTimingRejections}";
}
