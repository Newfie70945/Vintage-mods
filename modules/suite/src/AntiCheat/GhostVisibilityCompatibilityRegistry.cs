using System;
using System.Collections.Concurrent;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Tracks clients that explicitly advertised the runtime-safe ghost visibility implementation.
/// This is deliberately capability-based rather than version-string based so old Client Integrity
/// builds can never be sent the hide/restore packet that used to JIT-crash on Entity.Pos ABI drift.
/// </summary>
internal static class GhostVisibilityCompatibilityRegistry
{
    private static readonly ConcurrentDictionary<string, byte> SafePlayers = new(StringComparer.OrdinalIgnoreCase);

    public static void Update(string? playerUid, int supportedScanners)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return;
        bool safe = (((NewfiesAntiCheat.Shared.ClientScannerFlags)supportedScanners) &
                     NewfiesAntiCheat.Shared.ClientScannerFlags.GhostVisibilitySafe) != 0;
        if (safe) SafePlayers[playerUid] = 1;
        else SafePlayers.TryRemove(playerUid, out _);
    }

    public static bool IsSafe(string? playerUid) =>
        !string.IsNullOrWhiteSpace(playerUid) && SafePlayers.ContainsKey(playerUid);

    public static void Remove(string? playerUid)
    {
        if (!string.IsNullOrWhiteSpace(playerUid)) SafePlayers.TryRemove(playerUid, out _);
    }
}
