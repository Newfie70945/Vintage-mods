using System;

namespace BlockLogger.AntiCheat;

internal sealed class PacketIngressBudget
{
	private double tokens;

	private long lastTimestamp;

	private bool initialized;

	internal bool TryConsume(long monotonicMilliseconds, int perSecond, int burst)
	{
		perSecond = Math.Max(1, perSecond);
		burst = Math.Max(1, burst);
		if (!initialized)
		{
			initialized = true;
			tokens = burst;
			lastTimestamp = monotonicMilliseconds;
		}
		else if (monotonicMilliseconds > lastTimestamp)
		{
			tokens = Math.Min(burst, tokens + (double)(monotonicMilliseconds - lastTimestamp) * (double)perSecond / 1000.0);
			lastTimestamp = monotonicMilliseconds;
		}
		tokens = Math.Min(tokens, burst);
		if (tokens < 1.0)
		{
			return false;
		}
		tokens--;
		return true;
	}
}
