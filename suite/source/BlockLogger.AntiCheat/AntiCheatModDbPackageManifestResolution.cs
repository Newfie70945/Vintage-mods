using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbPackageManifestResolution
{
	public string ContentSha256 { get; set; } = "";

	public int FileCount { get; set; }

	public bool ManifestComplete { get; set; }

	public List<AntiCheatModDbFileFingerprintResolution> Files { get; set; } = new List<AntiCheatModDbFileFingerprintResolution>();
}
