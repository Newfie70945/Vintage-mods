namespace BlockLogger.AntiCheat;

public sealed class AntiCheatClientCompanionBaseline
{
	public string ModId { get; init; } = "";

	public string Version { get; init; } = "";

	public string SourceFileName { get; init; } = "";

	public string PackageSha256 { get; init; } = "";

	public string ContentSha256 { get; init; } = "";

	public long PackageBytes { get; init; }

	public int DllCount { get; init; }

	public long DllBytes { get; init; }

	public string AssemblyName { get; init; } = "NewfiesAntiCheatClient";

	public string AssemblyFileName { get; init; } = "NewfiesAntiCheatClient.dll";

	public string AssemblySha256 { get; init; } = "";

	public long AssemblyFileSizeBytes { get; init; }

	public string ModuleVersionId { get; init; } = "";

	public string BuildManifestSha256 { get; init; } = "";

	public string BuildManifestSignatureSha256 { get; init; } = "";

	public string BuildId { get; init; } = "";

	public bool BuildManifestSignatureVerified { get; init; }
}
