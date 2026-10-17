using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatCanonicalModFingerprint
{
	public string ModId { get; init; } = "";

	public string Version { get; init; } = "";

	public string PackageSha256 { get; init; } = "";

	public string ContentSha256 { get; init; } = "";

	public string SourceFileName { get; init; } = "";

	public long PackageBytes { get; init; }

	public int DllCount { get; init; }

	public long DllBytes { get; init; }

	public List<AntiCheatCanonicalDllFingerprint> Assemblies { get; init; } = new List<AntiCheatCanonicalDllFingerprint>();
}
