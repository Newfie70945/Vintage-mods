using System;
using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

internal sealed class ExpiringDiagnosticDedupe
{
	internal const int Capacity = 4096;

	private readonly object gate = new object();

	private readonly Dictionary<(string Code, string Uid), long> stamps = new Dictionary<(string, string), long>();

	private long lastPruneUtc;

	public bool IsDuplicate(string code, string uid, long nowUtc, int windowMs)
	{
		(string, string) key = (code.ToLowerInvariant(), uid.ToLowerInvariant());
		lock (gate)
		{
			if (nowUtc < lastPruneUtc || nowUtc - lastPruneUtc >= 10000)
			{
				foreach (KeyValuePair<(string, string), long> item in new List<KeyValuePair<(string, string), long>>(stamps))
				{
					if (nowUtc - item.Value >= 10000 || item.Value > nowUtc)
					{
						stamps.Remove(item.Key);
					}
				}
				lastPruneUtc = nowUtc;
			}
			bool result = stamps.TryGetValue(key, out var value) && nowUtc >= value && nowUtc - value < windowMs;
			if (stamps.ContainsKey(key) || stamps.Count < 4096)
			{
				stamps[key] = nowUtc;
			}
			return result;
		}
	}

	public void RemovePlayer(string uid)
	{
		lock (gate)
		{
			foreach (var item in new List<(string, string)>(stamps.Keys))
			{
				if (string.Equals(item.Item2, uid, StringComparison.OrdinalIgnoreCase))
				{
					stamps.Remove(item);
				}
			}
		}
	}

	public void Clear()
	{
		lock (gate)
		{
			stamps.Clear();
			lastPruneUtc = 0L;
		}
	}
}
