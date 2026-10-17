using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatEvidencePackageWebSnapshot
{
	public string FileName { get; set; } = "";

	public string ModId { get; set; } = "";

	public string Version { get; set; } = "";

	public string OriginalFileName { get; set; } = "";

	public string OwnerPlayerUid { get; set; } = "";

	public string OwnerPlayerName { get; set; } = "";

	public long CollectedUtc { get; set; }

	public long AuthorizationUtc { get; set; }

	public string AuthorizationMethod { get; set; } = "";

	public string DeclinePolicyAction { get; set; } = "TempBan";

	public int DeclinePolicyHours { get; set; }

	public string RequestedBy { get; set; } = "";

	public string TransferRequestId { get; set; } = "";

	public long Bytes { get; set; }

	public string Sha256 { get; set; } = "";

	public string ExpectedPackageSha256 { get; set; } = "";

	public bool GeneratedArchive { get; set; }

	public string OfficialDiffStatus { get; set; } = "NotAnalyzed";

	public string OfficialDiffSummary { get; set; } = "";

	public string OfficialDiffFileName { get; set; } = "";

	public int OfficialDiffCandidateCount { get; set; }

	public int OfficialDiffMatchedFiles { get; set; }

	public int OfficialDiffAddedFiles { get; set; }

	public int OfficialDiffRemovedFiles { get; set; }

	public int OfficialDiffModifiedFiles { get; set; }

	public bool OfficialDiffManifestComplete { get; set; }

	public long OfficialDiffAnalyzedUtc { get; set; }

	public List<string> OfficialDiffAddedPaths { get; set; } = new List<string>();

	public List<string> OfficialDiffRemovedPaths { get; set; } = new List<string>();

	public List<string> OfficialDiffModifiedPaths { get; set; } = new List<string>();
}
