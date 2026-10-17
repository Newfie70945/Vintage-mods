namespace BlockLogger.AntiCheat;

public sealed class AntiCheatModWebSnapshot
{
	public string ModId { get; set; } = "";

	public string Version { get; set; } = "";

	public string ModType { get; set; } = "";

	public string FileName { get; set; } = "";

	public string Sha256 { get; set; } = "";

	public string ContentSha256 { get; set; } = "";

	public long PackageBytes { get; set; }

	public int DllCount { get; set; }

	public long DllBytes { get; set; }

	public bool IsCodeMod { get; set; }

	public string SourceType { get; set; } = "";

	public bool LoadedOnServer { get; set; }

	public bool ServerBaselineAvailable { get; set; }

	public string ServerBaselineVersion { get; set; } = "";

	public string ServerBaselineFileName { get; set; } = "";

	public string ServerBaselinePackageSha256 { get; set; } = "";

	public string ServerBaselineContentSha256 { get; set; } = "";

	public long ServerBaselinePackageBytes { get; set; }

	public int ServerBaselineDllCount { get; set; }

	public long ServerBaselineDllBytes { get; set; }

	public string ServerVersionComparison { get; set; } = "Unavailable";

	public string ServerPackageComparison { get; set; } = "Unavailable";

	public string ServerContentComparison { get; set; } = "Unavailable";

	public string ServerDllLayoutComparison { get; set; } = "Unavailable";

	public string ServerAssemblyComparison { get; set; } = "Unavailable";

	public string ServerBaselineVerdict { get; set; } = "Unavailable";

	public string ServerBaselineMessage { get; set; } = "";

	public string ServerOfficialComparison { get; set; } = "Unavailable";

	public string ModDbStatus { get; set; } = "NotChecked";

	public string ModDbMessage { get; set; } = "";

	public long ModDbCheckedUtc { get; set; }

	public string OfficialVersion { get; set; } = "";

	public string OfficialFileName { get; set; } = "";

	public string OfficialPackageSha256 { get; set; } = "";

	public string OfficialContentSha256 { get; set; } = "";

	public long OfficialPackageBytes { get; set; }

	public int OfficialDllCount { get; set; }

	public long OfficialDllBytes { get; set; }

	public int OfficialCandidateCount { get; set; }

	public bool ComparedAgainstOfficial { get; set; }

	public string ComparisonBasis { get; set; } = "";

	public string PackageComparison { get; set; } = "Unavailable";

	public string ContentComparison { get; set; } = "Unavailable";

	public string DllLayoutComparison { get; set; } = "Unavailable";

	public string AssemblyComparison { get; set; } = "Unavailable";

	public bool ApprovedCustomFingerprint { get; set; }

	public string ApprovedCustomFingerprintId { get; set; } = "";

	public string ApprovedCustomLabel { get; set; } = "";

	public string ApprovedCustomNotes { get; set; } = "";
}
