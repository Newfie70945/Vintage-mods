namespace BlockLogger.AntiCheat;

public sealed class AntiCheatRiskFamilyWebSnapshot
{
	public string Family { get; set; } = "";

	public int Score { get; set; }

	public string Status { get; set; } = "Clean";

	public int ReviewThreshold { get; set; }

	public int SuspiciousThreshold { get; set; }

	public int DecaySeconds { get; set; }

	public long LastEvidenceUtc { get; set; }
}
