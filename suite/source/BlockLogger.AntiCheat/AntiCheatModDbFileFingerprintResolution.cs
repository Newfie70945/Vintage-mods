namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModDbFileFingerprintResolution
{
	public string Path { get; set; } = "";

	public string Sha256 { get; set; } = "";

	public long Bytes { get; set; }
}
