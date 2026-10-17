using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatForbiddenFingerprintWebSnapshot
{
	public string ModId { get; set; } = "";

	public bool HasPackageHash { get; set; }

	public bool HasContentHash { get; set; }

	public string PackageSha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public string Source { get; set; } = "";

	public string ResolvedVersion { get; set; } = "";

	public string ResolvedFileName { get; set; } = "";

	public long ResolvedUtc { get; set; }

	public int AssemblyNameCount { get; set; }

	public int AssemblyHashCount { get; set; }

	public int AssemblyStructureHashCount { get; set; }

	public int AssemblyBehaviorHashCount { get; set; }

	public int ReleaseCount { get; set; }

	public long ResolvedPackageBytes { get; set; }

	public int ResolvedDllCount { get; set; }

	public long ResolvedDllBytes { get; set; }

	public List<string> ResolvedVersions { get; set; } = new List<string>();

	public int SimilarityAssemblyCount { get; set; }

	public int ManualEntryCount { get; set; }

	public List<AntiCheatManualFingerprintWebSnapshot> ManualEntries { get; set; } = new List<AntiCheatManualFingerprintWebSnapshot>();
}
