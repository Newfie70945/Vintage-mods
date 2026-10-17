using System;

namespace BlockLogger.AntiCheat;

internal sealed class OreShieldWorkBudget
{
	public int Remaining { get; private set; }

	public int Used { get; private set; }

	public OreShieldWorkBudget(int limit)
	{
		Remaining = Math.Max(0, limit);
	}

	public bool TryTake()
	{
		if (Remaining == 0)
		{
			return false;
		}
		Remaining--;
		Used++;
		return true;
	}
}
