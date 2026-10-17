using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbFingerprintResolution
{
	public bool Success { get; set; }

	public string ModId { get; set; } = "";

	public string Version { get; set; } = "";

	public string FileName { get; set; } = "";

	public string DownloadUrl { get; set; } = "";

	public string PackageSha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public List<string> AssemblyNames { get; set; } = new List<string>();

	public List<string> AssemblySha256s { get; set; } = new List<string>();

	public List<AntiCheatModDbReleaseFingerprintResolution> Releases { get; set; } = new List<AntiCheatModDbReleaseFingerprintResolution>();

	public string Message { get; set; } = "";
}
