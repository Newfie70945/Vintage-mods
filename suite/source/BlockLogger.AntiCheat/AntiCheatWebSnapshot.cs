using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatWebSnapshot
{
	public bool Enabled { get; set; }

	public bool Available { get; set; }

	public string AvailabilityReason { get; set; } = "";

	public string LastActivationError { get; set; } = "";

	public string NetworkChannelName { get; set; } = "";

	public int NetworkPacketsDropped { get; set; }

	public bool RequireClient { get; set; }

	public bool ModDbAutomaticConsequencesEnabled { get; set; }

	public bool ClientModDbVerificationEnabled { get; set; }

	public bool StrictClientModDbEnforcementEnabled { get; set; }

	public bool StartupIntegrityGateEnabled { get; set; }

	public bool StartupIntegrityGateBlockWorldInteractions { get; set; }

	public bool StartupIntegrityGateRequireClean { get; set; }

	public bool StartupIntegrityGateDisconnectOnFailure { get; set; }

	public int StartupIntegrityGateNoticeCooldownSeconds { get; set; }

	public bool StartupCoreAssemblyInspectionEnabled { get; set; }

	public int StartupCoreAssemblyCount { get; set; }

	public bool ServerCompanionInstalled { get; set; }

	public bool ServerCompanionVersionMatches { get; set; }

	public string ServerCompanionVersion { get; set; } = "";

	public string ServerCompanionModId { get; set; } = "";

	public string ExpectedClientVersion { get; set; } = "";

	public int ProtocolVersion { get; set; }

	public int ChallengeTimeoutSeconds { get; set; }

	public int StartupChallengeTimeoutSeconds { get; set; }

	public int StartupChallengeHeartbeatExtensionSeconds { get; set; }

	public int StartupChallengeHardLimitSeconds { get; set; }

	public int ClientHelloGraceSeconds { get; set; }

	public int ClientHelloDisconnectDelaySeconds { get; set; }

	public int HeartbeatTimeoutSeconds { get; set; }

	public int RescanMinutes { get; set; }

	public bool RuntimeRescanOnChange { get; set; }

	public int RuntimeChangeRescanCooldownSeconds { get; set; }

	public List<string> ForbiddenModIds { get; set; } = new List<string>();

	public int FingerprintReleaseDepth { get; set; } = 6;

	public string ActiveBlacklistProfileId { get; set; } = "";

	public List<AntiCheatBlacklistProfileWebSnapshot> BlacklistProfiles { get; set; } = new List<AntiCheatBlacklistProfileWebSnapshot>();

	public List<string> RequiredClientModIds { get; set; } = new List<string>();

	public bool RequireApprovedClientBuild { get; set; }

	public bool RequireHardenedClientCapabilities { get; set; }

	public bool RequireCurrentClientRelease { get; set; }

	public List<string> ApprovedClientBuildHashes { get; set; } = new List<string>();

	public bool SimilarityDetectionEnabled { get; set; }

	public int SimilarityReviewThresholdPercent { get; set; }

	public int SimilarityHighThresholdPercent { get; set; }

	public bool RuntimeSurfaceInspectionEnabled { get; set; }

	public bool SuspiciousHotkeyInspectionEnabled { get; set; }

	public bool SuspiciousClientCommandInspectionEnabled { get; set; }

	public bool CanonicalServerModIntegrityEnabled { get; set; }

	public bool SuspectPackageCollectionEnabled { get; set; }

	public int SuspectPackageCollectionMaxMb { get; set; }

	public int SuspectPackageCollectionTimeoutSeconds { get; set; }

	public int SuspectPackageUploadKbps { get; set; }

	public string SuspectPackageDeclineAction { get; set; } = "TempBan";

	public int SuspectPackageDeclineTempBanHours { get; set; } = 24;

	public int SuspectPackageRetentionDays { get; set; } = 15;

	public string SuspectPackageFolder { get; set; } = "";

	public int SuspectPackageStoredCount { get; set; }

	public long SuspectPackageStoredBytes { get; set; }

	public bool ProtectedAssetInspectionEnabled { get; set; }

	public List<string> SuspiciousRuntimeTerms { get; set; } = new List<string>();

	public int BlacklistedRuntimeTermCount { get; set; }

	public List<string> ProtectedAssetPatterns { get; set; } = new List<string>();

	public int CanonicalServerModCount { get; set; }

	public List<AntiCheatForbiddenFingerprintWebSnapshot> ForbiddenFingerprints { get; set; } = new List<AntiCheatForbiddenFingerprintWebSnapshot>();

	public List<AntiCheatConsequenceRuleWebSnapshot> ConsequenceRules { get; set; } = new List<AntiCheatConsequenceRuleWebSnapshot>();

	public AntiCheatPacketGuardWebSnapshot PacketGuard { get; set; } = new AntiCheatPacketGuardWebSnapshot();

	public OreShieldWebSnapshot OreShield { get; set; } = new OreShieldWebSnapshot();

	public AntiCheatHistorySummaryWebSnapshot History { get; set; } = new AntiCheatHistorySummaryWebSnapshot();

	public int OnlinePlayers { get; set; }

	public int TrackedPlayers { get; set; }

	public int ClientsConnected { get; set; }

	public int Clean { get; set; }

	public int Review { get; set; }

	public int Suspicious { get; set; }

	public int AwaitingOrScanning { get; set; }

	public int MissingOrTimedOut { get; set; }

	public AntiCheatModDbHealthWebSnapshot ModDbHealth { get; set; } = new AntiCheatModDbHealthWebSnapshot();

	public List<AntiCheatApprovedClientModFingerprintWebSnapshot> ApprovedClientModFingerprints { get; set; } = new List<AntiCheatApprovedClientModFingerprintWebSnapshot>();

	public List<AntiCheatConnectionDiagnosticWebSnapshot> RecentConnectionDiagnostics { get; set; } = new List<AntiCheatConnectionDiagnosticWebSnapshot>();

	public List<AntiCheatPlayerWebSnapshot> Players { get; set; } = new List<AntiCheatPlayerWebSnapshot>();
}
