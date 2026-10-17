namespace BlockLogger.AntiCheat;

public sealed class AntiCheatCoreAssemblyFingerprint
{
	public string AssemblyName { get; init; } = "";

	public string Version { get; init; } = "";

	public string Sha256 { get; init; } = "";

	public string ModuleVersionId { get; init; } = "";
}
