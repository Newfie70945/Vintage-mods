namespace BlockLogger.AntiCheat;

public sealed class AntiCheatCanonicalDllFingerprint
{
	public string FileName { get; init; } = "";

	public long FileSizeBytes { get; init; }

	public string Sha256 { get; init; } = "";
}
