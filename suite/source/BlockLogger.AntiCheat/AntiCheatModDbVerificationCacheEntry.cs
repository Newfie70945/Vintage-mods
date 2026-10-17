using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbVerificationCacheEntry
{
	public string ModId { get; set; } = "";

	public string Version { get; set; } = "";

	public string Status { get; set; } = "Pending";

	public string Message { get; set; } = "";

	public long CheckedUtc { get; set; }

	public AntiCheatModDbReleaseFingerprintResolution? Release { get; set; }

	public List<AntiCheatModDbReleaseFingerprintResolution> Releases { get; set; } = new List<AntiCheatModDbReleaseFingerprintResolution>();
}
