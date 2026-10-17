using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatManualFingerprintSaveRequest
{
	public string Action { get; set; } = "save";

	public string ModId { get; set; } = "";

	public string EntryId { get; set; } = "";

	public string Label { get; set; } = "";

	public string Version { get; set; } = "";

	public string FileName { get; set; } = "";

	public string Notes { get; set; } = "";

	public string PackageSha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public List<string> AssemblySha256s { get; set; } = new List<string>();

	public List<string> AssemblyMvids { get; set; } = new List<string>();

	public List<string> AssemblyStructureSha256s { get; set; } = new List<string>();

	public List<string> AssemblyBehaviorSha256s { get; set; } = new List<string>();
}
