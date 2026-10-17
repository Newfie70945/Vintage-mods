using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatPlayerWebSnapshot
{
	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public object? Decision { get; set; }

	public string Status { get; set; } = "Unknown";

	public int RiskScore { get; set; }

	public bool StartupGatePending { get; set; }

	public bool StartupIntegrityVerified { get; set; }

	public long StartupGateStartedUtc { get; set; }

	public long StartupGateCompletedUtc { get; set; }

	public string StartupGateVerdict { get; set; } = "NotStarted";

	public int StartupGateBlockedActions { get; set; }

	public List<AntiCheatRiskFamilyWebSnapshot> RiskFamilies { get; set; } = new List<AntiCheatRiskFamilyWebSnapshot>();

	public bool ClientInstalled { get; set; }

	public string ClientVersion { get; set; } = "";

	public bool PackageMetricsSupported { get; set; }

	public bool ReportHashV2Supported { get; set; }

	public bool BoundHeartbeatSupported { get; set; }

	public bool HardenedCapabilitiesSupported { get; set; }

	public bool ChallengeQueueingSupported { get; set; }

	public bool CurrentClientReleaseSupported { get; set; }

	public string GameVersion { get; set; } = "";

	public string Platform { get; set; } = "";

	public int ModCount { get; set; }

	public int ReportedModCount { get; set; }

	public int AssemblyCount { get; set; }

	public string ClientAssemblySha256 { get; set; } = "";

	public string ClientPackageSha256 { get; set; } = "";

	public string ClientAssemblyMvid { get; set; } = "";

	public int RuntimeGeneration { get; set; }

	public string RuntimeDigest { get; set; } = "";

	public int RuntimeBaselineAssemblyCount { get; set; }

	public int RuntimeLoadedAssemblyCount { get; set; }

	public int RuntimeDynamicAssemblyCount { get; set; }

	public int RuntimeNewAssemblyCount { get; set; }

	public int RuntimeUnassociatedNewAssemblyCount { get; set; }

	public int HarmonyPatchCount { get; set; }

	public int SuspiciousHarmonyOwnerCount { get; set; }

	public string HarmonyPatchDigest { get; set; } = "";

	public int FindingCount { get; set; }

	public int StrongFindingCount { get; set; }

	public int HighConfidenceFindingCount { get; set; }

	public int ServerAuthoritativeFindingCount { get; set; }

	public long LastScanUtc { get; set; }

	public long LastHeartbeatUtc { get; set; }

	public long NextScanUtc { get; set; }

	public int NetworkPacketsDropped { get; set; }

	public string LastConsequence { get; set; } = "";

	public long LastConsequenceUtc { get; set; }

	public List<AntiCheatFindingWebSnapshot> Findings { get; set; } = new List<AntiCheatFindingWebSnapshot>();

	public List<AntiCheatModWebSnapshot> Mods { get; set; } = new List<AntiCheatModWebSnapshot>();

	public List<AntiCheatAssemblyWebSnapshot> Assemblies { get; set; } = new List<AntiCheatAssemblyWebSnapshot>();

	public List<AntiCheatRuntimeAssemblyWebSnapshot> RuntimeNewAssemblies { get; set; } = new List<AntiCheatRuntimeAssemblyWebSnapshot>();

	public List<AntiCheatScannerMetricWebSnapshot> ScannerMetrics { get; set; } = new List<AntiCheatScannerMetricWebSnapshot>();

	public List<AntiCheatConnectionDiagnosticWebSnapshot> RecentConnectionDiagnostics { get; set; } = new List<AntiCheatConnectionDiagnosticWebSnapshot>();

	public List<AntiCheatEvidenceCandidateWebSnapshot> EvidenceCandidates { get; set; } = new List<AntiCheatEvidenceCandidateWebSnapshot>();

	public List<AntiCheatEvidencePackageWebSnapshot> CollectedEvidence { get; set; } = new List<AntiCheatEvidencePackageWebSnapshot>();
}
