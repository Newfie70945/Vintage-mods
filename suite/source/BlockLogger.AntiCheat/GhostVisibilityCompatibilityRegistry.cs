using System;
using System.Collections.Concurrent;

namespace BlockLogger.AntiCheat;

internal static class GhostVisibilityCompatibilityRegistry
{
	private static readonly ConcurrentDictionary<string, byte> SafePlayers = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	public static void Update(string? playerUid, int supportedScanners)
	{
		if (!string.IsNullOrWhiteSpace(playerUid))
		{
			if ((supportedScanners & 0x20000) != 0)
			{
				SafePlayers[playerUid] = 1;
			}
			else
			{
				SafePlayers.TryRemove(playerUid, out var _);
			}
		}
	}

	public static bool IsSafe(string? playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && SafePlayers.ContainsKey(playerUid);
	}

	public static void Remove(string? playerUid)
	{
		if (!string.IsNullOrWhiteSpace(playerUid))
		{
			SafePlayers.TryRemove(playerUid, out var _);
		}
	}
}
