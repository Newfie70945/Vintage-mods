using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbVerificationCacheStore
{
    public int SchemaVersion { get; set; } = 3;
    public List<AntiCheatModDbVerificationCacheEntry> Entries { get; set; } = new();
}

public sealed class AntiCheatModDbVerificationCacheEntry
{
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Message { get; set; } = "";
    public long CheckedUtc { get; set; }
    // Release is retained for backward compatibility with schema-1 cache files.
    public AntiCheatModDbReleaseFingerprintResolution? Release { get; set; }
    public List<AntiCheatModDbReleaseFingerprintResolution> Releases { get; set; } = new();
}
