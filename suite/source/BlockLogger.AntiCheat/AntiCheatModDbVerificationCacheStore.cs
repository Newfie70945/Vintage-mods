using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbVerificationCacheStore
{
	public int SchemaVersion { get; set; } = 3;

	public List<AntiCheatModDbVerificationCacheEntry> Entries { get; set; } = new List<AntiCheatModDbVerificationCacheEntry>();
}
