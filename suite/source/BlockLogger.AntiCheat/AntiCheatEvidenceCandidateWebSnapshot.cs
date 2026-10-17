namespace BlockLogger.AntiCheat;

public sealed class AntiCheatEvidenceCandidateWebSnapshot
{
	public string ModId { get; set; } = "";

	public string Version { get; set; } = "";

	public string FileName { get; set; } = "";

	public long PackageBytes { get; set; }

	public int DllCount { get; set; }

	public long DllBytes { get; set; }

	public string Reason { get; set; } = "";

	public int Severity { get; set; }

	public bool AlreadyCollected { get; set; }

	public bool TransferInProgress { get; set; }

	public string LastCollectedFile { get; set; } = "";
}
