using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.AntiCheat;

internal sealed class ConfirmedSpeedRisk
{
	private readonly Queue<long> observations = new Queue<long>();

	internal static bool Eligible(string code, int severity, string? evidence)
	{
		return code.Equals("PACKET_SPEED_HACK", StringComparison.OrdinalIgnoreCase) && severity == 4 && (";" + evidence + ";").Contains(";speedEscalationEligible=True;", StringComparison.Ordinal);
	}

	internal int Observe(long now)
	{
		Prune(now);
		if (observations.Count == 0 || now - observations.Last() >= 10000)
		{
			if (observations.Count == 3)
			{
				observations.Dequeue();
			}
			observations.Enqueue(now);
		}
		return observations.Count;
	}

	internal int Count(long now)
	{
		Prune(now);
		return observations.Count;
	}

	internal int Bonus(long now, int increment)
	{
		return Math.Clamp((Count(now) - 1) * increment, 0, 50);
	}

	private void Prune(long now)
	{
		if (observations.Count > 0 && now < observations.Last())
		{
			observations.Clear();
		}
		while (observations.Count > 0 && now - observations.Peek() >= 60000)
		{
			observations.Dequeue();
		}
	}
}
