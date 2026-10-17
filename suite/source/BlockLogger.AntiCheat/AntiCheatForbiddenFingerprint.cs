using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatForbiddenFingerprint
{
	public string ModId { get; set; } = "";

	public string Source { get; set; } = "";

	public string ResolvedVersion { get; set; } = "";

	public string ResolvedFileName { get; set; } = "";

	public long ResolvedUtc { get; set; }

	public string PackageSha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public List<string> AssemblyNames { get; set; } = new List<string>();

	public List<string> AssemblySha256s { get; set; } = new List<string>();

	public List<string> AssemblyMvids { get; set; } = new List<string>();

	public List<string> AssemblyStructureSha256s { get; set; } = new List<string>();

	public List<string> AssemblyBehaviorSha256s { get; set; } = new List<string>();

	public List<string> ModSystemTypeNames { get; set; } = new List<string>();

	public List<AntiCheatReleaseFingerprint> Releases { get; set; } = new List<AntiCheatReleaseFingerprint>();

	public List<AntiCheatManualFingerprintEntry> ManualEntries { get; set; } = new List<AntiCheatManualFingerprintEntry>();
}
