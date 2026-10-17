using System;
using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

// Diagnostic duplicate suppression is temporary state, not a player history store.
internal sealed class ExpiringDiagnosticDedupe
{
    internal const int Capacity = 4096;
    private readonly object gate = new();
    private readonly Dictionary<(string Code, string Uid), long> stamps = new();
    private long lastPruneUtc;

    public bool IsDuplicate(string code, string uid, long nowUtc, int windowMs)
    {
        var key = (code.ToLowerInvariant(), uid.ToLowerInvariant());
        lock (gate)
        {
            // All callers use windows <= 10 seconds. Expire idle identities even if
            // they never reconnect, and bound a burst of previously unseen identities.
            if (nowUtc < lastPruneUtc || nowUtc - lastPruneUtc >= 10_000)
            {
                foreach (var row in new List<KeyValuePair<(string Code, string Uid), long>>(stamps))
                    if (nowUtc - row.Value >= 10_000 || row.Value > nowUtc) stamps.Remove(row.Key);
                lastPruneUtc = nowUtc;
            }
            bool duplicate = stamps.TryGetValue(key, out long previous) &&
                nowUtc >= previous && nowUtc - previous < windowMs;
            if (stamps.ContainsKey(key) || stamps.Count < Capacity) stamps[key] = nowUtc;
            // At capacity an untracked diagnostic may be counted twice; never suppress
            // a real detector finding or discard a trusted-transition grant.
            return duplicate;
        }
    }

    public void RemovePlayer(string uid)
    {
        lock (gate)
            foreach (var key in new List<(string Code, string Uid)>(stamps.Keys))
                if (string.Equals(key.Uid, uid, StringComparison.OrdinalIgnoreCase)) stamps.Remove(key);
    }

    public void Clear() { lock (gate) { stamps.Clear(); lastPruneUtc = 0; } }
}
