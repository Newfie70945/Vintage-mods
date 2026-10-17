using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatAssemblyWebSnapshot
{
	public string AssemblyName { get; set; } = "";

	public string Version { get; set; } = "";

	public string FileName { get; set; } = "";

	public string Sha256 { get; set; } = "";

	public long FileSizeBytes { get; set; }

	public string ModuleVersionId { get; set; } = "";

	public List<string> AssociatedModIds { get; set; } = new List<string>();

	public string StructureSha256 { get; set; } = "";

	public List<string> ModSystemTypeNames { get; set; } = new List<string>();

	public int StructureTokenCount { get; set; }

	public string BehaviorSha256 { get; set; } = "";

	public int BehaviorTokenCount { get; set; }
}
