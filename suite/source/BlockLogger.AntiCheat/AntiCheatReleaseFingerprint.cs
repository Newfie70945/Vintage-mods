using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatReleaseFingerprint
{
	public string Version { get; set; } = "";

	public string FileName { get; set; } = "";

	public string PackageSha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public long PackageBytes { get; set; }

	public int DllCount { get; set; }

	public long DllBytes { get; set; }

	public long ResolvedUtc { get; set; }

	public List<AntiCheatReleaseAssemblyFingerprint> Assemblies { get; set; } = new List<AntiCheatReleaseAssemblyFingerprint>();
}
