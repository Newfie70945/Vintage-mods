using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatReleaseAssemblyFingerprint
{
	public string AssemblyName { get; set; } = "";

	public string Sha256 { get; set; } = "";

	public int StructureTokenCount { get; set; }

	public List<ulong> SimilarityMinHash { get; set; } = new List<ulong>();

	public string BehaviorSha256 { get; set; } = "";

	public int BehaviorTokenCount { get; set; }

	public List<ulong> BehaviorMinHash { get; set; } = new List<ulong>();

	public long FileSizeBytes { get; set; }
}
