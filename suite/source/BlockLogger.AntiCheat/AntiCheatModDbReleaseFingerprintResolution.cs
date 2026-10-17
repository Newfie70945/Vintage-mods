using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbReleaseFingerprintResolution
{
	public string Version { get; set; } = "";

	public string FileName { get; set; } = "";

	public string DownloadUrl { get; set; } = "";

	public string PackageSha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public long PackageBytes { get; set; }

	public int DllCount { get; set; }

	public long DllBytes { get; set; }

	public int FileCount { get; set; }

	public bool ManifestComplete { get; set; }

	public List<AntiCheatModDbFileFingerprintResolution> Files { get; set; } = new List<AntiCheatModDbFileFingerprintResolution>();

	public List<AntiCheatModDbAssemblyFingerprintResolution> Assemblies { get; set; } = new List<AntiCheatModDbAssemblyFingerprintResolution>();
}
