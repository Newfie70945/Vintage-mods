using System.Collections.Generic;

namespace BlockLogger.AntiCheat;


public sealed class AntiCheatNavStatusWebSnapshot
{
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public int Suspicious { get; set; }
    public int Review { get; set; }
    public int MissingOrTimedOut { get; set; }
    public AntiCheatNavPacketGuardWebSnapshot PacketGuard { get; set; } = new();
}

public sealed class AntiCheatNavPacketGuardWebSnapshot
{
    public bool Enabled { get; set; }
}

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
    public List<string> ForbiddenModIds { get; set; } = new();
    public int FingerprintReleaseDepth { get; set; } = 6;
    public string ActiveBlacklistProfileId { get; set; } = "";
    public List<AntiCheatBlacklistProfileWebSnapshot> BlacklistProfiles { get; set; } = new();
    public List<string> RequiredClientModIds { get; set; } = new();
    public bool RequireApprovedClientBuild { get; set; }
    public bool RequireHardenedClientCapabilities { get; set; }
    public bool RequireCurrentClientRelease { get; set; }
    public List<string> ApprovedClientBuildHashes { get; set; } = new();
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
    public List<string> SuspiciousRuntimeTerms { get; set; } = new();
    public int BlacklistedRuntimeTermCount { get; set; }
    public List<string> ProtectedAssetPatterns { get; set; } = new();
    public int CanonicalServerModCount { get; set; }
    public List<AntiCheatForbiddenFingerprintWebSnapshot> ForbiddenFingerprints { get; set; } = new();
    public List<AntiCheatConsequenceRuleWebSnapshot> ConsequenceRules { get; set; } = new();
    public AntiCheatPacketGuardWebSnapshot PacketGuard { get; set; } = new();
    public OreShieldWebSnapshot OreShield { get; set; } = new();
    public AntiCheatHistorySummaryWebSnapshot History { get; set; } = new();
    public int OnlinePlayers { get; set; }
    public int TrackedPlayers { get; set; }
    public int ClientsConnected { get; set; }
    public int Clean { get; set; }
    public int Review { get; set; }
    public int Suspicious { get; set; }
    public int AwaitingOrScanning { get; set; }
    public int MissingOrTimedOut { get; set; }
    public AntiCheatModDbHealthWebSnapshot ModDbHealth { get; set; } = new();
    public List<AntiCheatApprovedClientModFingerprintWebSnapshot> ApprovedClientModFingerprints { get; set; } = new();
    public List<AntiCheatConnectionDiagnosticWebSnapshot> RecentConnectionDiagnostics { get; set; } = new();
    public List<AntiCheatPlayerWebSnapshot> Players { get; set; } = new();
}




public sealed class OreShieldWebSnapshot
{
    public double VisibilityLastMilliseconds { get; set; }
    public double VisibilityMaxMilliseconds { get; set; }
    public int PendingWorldChanges { get; set; }
    public int PendingAuthoritativeCells { get; set; }
    public long AuthoritativeCorrectionFlushes { get; set; }
    public int PlacedOreDirtyChunks { get; set; }
    public long PlacedOreCheckpoints { get; set; }
    public long PlacedOreCheckpointBytes { get; set; }
    public long WorldChangeCoalesces { get; set; }
    public long WorldChangeOverflows { get; set; }
    public long ExposureBuildCells { get; set; }

    public bool Enabled { get; set; }
    public bool Active { get; set; }
    public string Status { get; set; } = "";
    public int ProtectedOreVariants { get; set; }
    public int HostVariants { get; set; }
    public int SkippedOreCandidates { get; set; }
    public bool ProtectModdedOres { get; set; }
    public bool RevealOnUnknownNeighbor { get; set; }
    public bool StrictOreMatching { get; set; }
    public int RevealRadiusBlocks { get; set; } = 0;
    public bool RevealWholeVeinOnMine { get; set; } = false;
    public int MaxVeinRevealBlocks { get; set; } = 2048;
    public int DiscoveryRangeBlocks { get; set; } = 96;
    public int VisibilityScanIntervalMs { get; set; } = 250;
    public int MaxLosChecksPerPass { get; set; } = 96;
    public int ViewConeDegrees { get; set; } = 140;
    public int IdleRescanMs { get; set; } = 3000;
    public bool LeakAuditEnabled { get; set; } = true;
    public bool ChunkCacheEnabled { get; set; }
    public int ChunkCacheMiB { get; set; } = 96;
    public int MaxCachedChunks { get; set; } = 4096;
    public List<string> AdditionalOrePatterns { get; set; } = new();
    public List<string> ExcludedBlockPatterns { get; set; } = new();
    public int CacheEntries { get; set; }
    public long CacheBytes { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long ChunkMaskHookCalls { get; set; }
    public long ChunkMaskUniquePackets { get; set; }
    public long ChunkMaskEligiblePackets { get; set; }
    public long FailClosedChunkPackets { get; set; }
    public long FailClosedCells { get; set; }
    public long ChunksInspected { get; set; }
    public long ChunksMasked { get; set; }
    public long PassThroughChunks { get; set; }
    public long VirtualizedBlocks { get; set; }
    public long HiddenRealOreBlocks { get; set; }
    public long DecoyBlocks { get; set; }
    public long RewrittenUpdatePackets { get; set; }
    public long UnsupportedCompressionPackets { get; set; }
    public long OreIndexCacheHits { get; set; }
    public long OreIndexCacheMisses { get; set; }
    public int OreIndexCacheEntries { get; set; }
    public long RevisionInvalidations { get; set; }
    public long MiningRevealPulses { get; set; }
    public long MiningRevealCorrections { get; set; }
    public long ExposureSyncEvents { get; set; }
    public long ExposureBlocksEvaluated { get; set; }
    public long ExposureCorrectionsSent { get; set; }
    public int PendingVeinJobs { get; set; }
    public long OldestVeinJobAgeMs { get; set; }
    public long VeinLimitReachedJobs { get; set; }
    public long VeinDiscoveries { get; set; }
    public long VeinBlocksRevealed { get; set; }
    public long RevealRadiusPasses { get; set; }
    public long RevealPlayersChecked { get; set; }
    public long RevealDesiredBlocks { get; set; }
    public long RevealCorrectionPackets { get; set; }
    public long RevealRealBlockPackets { get; set; }
    public long ReconcealBlockPackets { get; set; }
    public long LosPasses { get; set; }
    public long LosPlayersChecked { get; set; }
    public long LosCandidatesConsidered { get; set; }
    public long LosRayChecks { get; set; }
    public long LosDiscoveries { get; set; }
    public long LosMovementSkips { get; set; }
    public long VisibilityChunksVisited { get; set; }
    public long VisibilityCandidatesEvaluated { get; set; }
    public long VisibilityIndexBuilds { get; set; }
    public long VisibilityBudgetDeferrals { get; set; }
    public int VisibilityPlayersPerTickLimit { get; set; }
    public int VisibilityChunksPerPlayerPassLimit { get; set; }
    public int VisibilityCandidatesPerPlayerPassLimit { get; set; }
    public int VisibilityIndexBuildsPerTickLimit { get; set; }
    public int BoundaryRefreshesPending { get; set; }
    public long BoundaryRefreshCellsEvaluated { get; set; }
    public long BoundaryRefreshOverflowInvalidations { get; set; }
    public int BoundaryRefreshesPerTickLimit { get; set; }
    public long ExposedIndexCandidates { get; set; }
    public long LeakAuditChecks { get; set; }
    public long LeakAuditViolations { get; set; }
    public int ClientSyncReadyClients { get; set; }
    public long ClientSyncHelloPackets { get; set; }
    public long ClientSyncRejectedHellos { get; set; }
    public long ClientSyncBatchesSent { get; set; }
    public long ClientSyncEntriesSent { get; set; }
    public long ClientSyncAcks { get; set; }
    public long ClientSyncDeferredAcks { get; set; }
    public long ClientSyncAppliedEntries { get; set; }
    public long ClientSyncFailedEntries { get; set; }
    public long ClientSyncSkippedEntries { get; set; }
    public long ClientSyncApplyFailureBatches { get; set; }
    public long ClientSyncFailureResyncs { get; set; }
    // Legacy field retained for Web API compatibility. Native SetBlock fallback is retired.
    public long ClientSyncFallbacks { get; set; }
    public long ClientSyncTimeouts { get; set; }
    public long ClientSyncRetries { get; set; }
    public long ClientSyncResyncRequests { get; set; }
    public int ClientSyncPendingBatches { get; set; }
    public int ClientSyncQueuedCells { get; set; }
    public long ClientSyncQueueCoalesces { get; set; }
    public long ClientSyncQueueOverflows { get; set; }
    public long ClientSyncStallDeferrals { get; set; }
    public int DiscoveryPlayers { get; set; }
    public long DiscoveryCells { get; set; }
    public long DiscoveryChunkRecords { get; set; }
    public int DiscoveryDistinctChunks { get; set; }
    public long DiscoveryInvalidationOwnersVisited { get; set; }
    public int RevisionEntries { get; set; }
    public long RevisionCompactions { get; set; }
    public int SyncProtocolVersion { get; set; }
    public string SyncCapability { get; set; } = "";
    public string SyncBuildMarker { get; set; } = "";
    public long RevealDispatchAttempts { get; set; }
    public long RevealRecipientResolveFailures { get; set; }
    public long RevealChunkNotSentSkips { get; set; }
    public long RevealStaleKeySkips { get; set; }
    public long RevealDirectPacketsSent { get; set; }
    public long RevealDirectPacketErrors { get; set; }
    public long RevealClientSyncUnsupportedSkips { get; set; }
    public long HookErrors { get; set; }
}

public sealed class AntiCheatOreShieldSaveRequest
{
    public bool Enabled { get; set; } = true;
    public bool ProtectModdedOres { get; set; } = true;
    public bool RevealOnUnknownNeighbor { get; set; } = false;
    public bool StrictOreMatching { get; set; } = true;
    public int RevealRadiusBlocks { get; set; } = 0;
    public bool RevealWholeVeinOnMine { get; set; } = false;
    public int MaxVeinRevealBlocks { get; set; } = 2048;
    public int DiscoveryRangeBlocks { get; set; } = 96;
    public int VisibilityScanIntervalMs { get; set; } = 250;
    public int MaxLosChecksPerPass { get; set; } = 96;
    public int ViewConeDegrees { get; set; } = 140;
    public int IdleRescanMs { get; set; } = 3000;
    public bool LeakAuditEnabled { get; set; } = true;
    public bool ChunkCacheEnabled { get; set; } = true;
    public int ChunkCacheMiB { get; set; } = 96;
    public int MaxCachedChunks { get; set; } = 4096;
    public List<string> AdditionalOrePatterns { get; set; } = new();
    public List<string> ExcludedBlockPatterns { get; set; } = new();
}

public sealed class AntiCheatApprovedClientModFingerprintWebSnapshot
{
    public string Id { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

public sealed class AntiCheatClientModApprovalRequest
{
    public string Action { get; set; } = "approve";
    public string Id { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class AntiCheatModDbHealthWebSnapshot
{
    public bool Enabled { get; set; }
    public int CacheEntries { get; set; }
    public int Pending { get; set; }
    public int Resolved { get; set; }
    public int NotFound { get; set; }
    public int Unavailable { get; set; }
    public int Fresh { get; set; }
    public int Stale { get; set; }
    public long LastCheckedUtc { get; set; }
    public long LastResolvedUtc { get; set; }
    public long LastUnavailableUtc { get; set; }
    public int MaxConcurrentRequests { get; set; }
    public int ServerBaselineTotal { get; set; }
    public int ServerBaselineFresh { get; set; }
    public int ServerBaselinePending { get; set; }
    public int ServerBaselineResolved { get; set; }
    public int ServerBaselineNotFound { get; set; }
    public int ServerBaselineUnavailable { get; set; }
    public int ServerBaselineOfficialMatches { get; set; }
    public int ServerBaselineCustomBuilds { get; set; }
    public int ServerBaselineDiagnosticDifferences { get; set; }
    public long ServerBaselineLastQueuedUtc { get; set; }
}

public sealed class AntiCheatConnectionDiagnosticWebSnapshot
{
    public long Utc { get; set; }
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Code { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Status { get; set; } = "";
    public bool ClientInstalled { get; set; }
    public string ClientVersion { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public long LastHelloUtc { get; set; }
    public long LastHeartbeatUtc { get; set; }
    public long StartupGateStartedUtc { get; set; }
    public string ChallengeId { get; set; } = "";
}

public sealed class AntiCheatClientScanSettingsSaveRequest
{
    public bool RequireClient { get; set; } = true;
    public bool ClientModDbVerificationEnabled { get; set; } = true;
    public bool StrictClientModDbEnforcementEnabled { get; set; } = true;
    public bool RequireHardenedClientCapabilities { get; set; } = false;
    public bool RequireCurrentClientRelease { get; set; } = false;
    public bool StartupIntegrityGateEnabled { get; set; } = true;
    public bool StartupIntegrityGateBlockWorldInteractions { get; set; } = false;
    public bool StartupIntegrityGateRequireClean { get; set; } = false;
    public bool StartupIntegrityGateDisconnectOnFailure { get; set; } = true;
    public int StartupIntegrityGateNoticeCooldownSeconds { get; set; } = 4;
    public int ChallengeTimeoutSeconds { get; set; } = 30;
    public int StartupChallengeTimeoutSeconds { get; set; } = 300;
    public int StartupChallengeHeartbeatExtensionSeconds { get; set; } = 120;
    public int StartupChallengeHardLimitSeconds { get; set; } = 900;
    public int ClientHelloGraceSeconds { get; set; } = 90;
    public int ClientHelloDisconnectDelaySeconds { get; set; } = 60;
    public int HeartbeatTimeoutSeconds { get; set; } = 180;
    public int RescanMinutes { get; set; } = 15;
    public bool RuntimeRescanOnChange { get; set; } = true;
    public int RuntimeChangeRescanCooldownSeconds { get; set; } = 30;
}

public sealed class AntiCheatEvidenceSettingsSaveRequest
{
    public bool Enabled { get; set; } = true;
    public int MaxMb { get; set; } = 96;
    public int TimeoutSeconds { get; set; } = 120;
    public int UploadKbps { get; set; } = 384;
    public string DeclineAction { get; set; } = "TempBan";
    public int DeclineTempBanHours { get; set; } = 24;
}

public sealed class AntiCheatEvidenceRequest
{
    public string Player { get; set; } = "";
    public string ModId { get; set; } = "";
}

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
    public List<string> OfficialDiffAddedPaths { get; set; } = new();
    public List<string> OfficialDiffRemovedPaths { get; set; } = new();
    public List<string> OfficialDiffModifiedPaths { get; set; } = new();
}

public sealed class AntiCheatEvidenceAnalyzeRequest
{
    public string FileName { get; set; } = "";
}

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

public sealed class AntiCheatModuleSaveRequest
{
    public bool Enabled { get; set; }
}

public sealed class AntiCheatGuiSettingsSaveRequest
{
    public bool RequireClient { get; set; } = true;
    public int RescanMinutes { get; set; } = 15;
    public bool RuntimeRescanOnChange { get; set; } = true;
    public int RuntimeChangeRescanCooldownSeconds { get; set; } = 30;
    public bool SimilarityDetectionEnabled { get; set; } = true;
    public int SimilarityReviewThresholdPercent { get; set; } = 72;
    public int SimilarityHighThresholdPercent { get; set; } = 88;
    public bool RuntimeSurfaceInspectionEnabled { get; set; } = true;
    public bool SuspiciousHotkeyInspectionEnabled { get; set; } = true;
    public bool SuspiciousClientCommandInspectionEnabled { get; set; } = true;
    public bool CanonicalServerModIntegrityEnabled { get; set; } = true;
    public bool ProtectedAssetInspectionEnabled { get; set; } = true;

    public bool PacketGuardEnabled { get; set; } = true;
    public bool MovementEnabled { get; set; } = true;
    public double HardMoveDistance { get; set; } = 32;
    public bool RejectUnauthorizedMovement { get; set; } = true;
    public bool Th3EssentialsSpawnCompatibility { get; set; } = true;
    public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;
    public bool ReachEnabled { get; set; } = true;
    public double ReachTolerance { get; set; } = 1.25;
    public double ReachHardExcess { get; set; } = 1.5;
    public bool RejectOutOfReachActions { get; set; } = true;

    public bool MiningEnabled { get; set; } = true;
    public bool MiningCadenceValidationEnabled { get; set; } = false;
    public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;
    public int MiningCadenceConfirmations { get; set; } = 3;
    public bool RejectConnectedBreakBursts { get; set; } = true;
    public bool RejectImpossibleMiningCadence { get; set; }

    public bool TpsAwarenessEnabled { get; set; } = true;
    public double TpsDegradedPercent { get; set; } = 85;
    public double TpsCriticalPercent { get; set; } = 60;
    public double TpsMaxTimingMultiplier { get; set; } = 2.5;
    public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;

    public bool IncidentCorrelationEnabled { get; set; } = true;
    public int IncidentCorrelationWindowSeconds { get; set; } = 90;
    public int IncidentCorrelationRequiredCategories { get; set; } = 2;
    public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;

    public bool HistoryEnabled { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90;
    public int HistoryMaxRecords { get; set; } = 500;
}

public sealed class AntiCheatPacketProtectionGuiSaveRequest
{
    public bool PacketGuardEnabled { get; set; } = true;
    public bool PlayerWarningEnabled { get; set; } = false;
    public int PlayerWarningCooldownSeconds { get; set; } = 15;
    public int AccelerationSampleMinMs { get; set; } = 220;
    public double AccelerationSmoothingFactor { get; set; } = 0.25;
    public bool NoclipEnabled { get; set; } = false;
    public int NoclipRequiredCollisionSamples { get; set; } = 3;
    public double NoclipMinPenetrationDepth { get; set; } = 0.18;
    public double NoclipEndpointMinNetMovement { get; set; } = 0.45;
    public double MountAccelerationMinSpeedRatio { get; set; } = 0.85;
    public bool DiagnosticContextEnabled { get; set; } = true;
    public bool DiagnosticSuppressionEnabled { get; set; } = true;
    public int DiagnosticSpeedHistorySamples { get; set; } = 8;

    public bool MovementEnabled { get; set; } = true;
    public int MovementSampleMs { get; set; } = 75;
    public double HardMoveDistance { get; set; } = 32.0;
    public int MovementConfirmSamples { get; set; } = 2;
    public int MovementConfirmWindowMs { get; set; } = 600;
    public double MovementExtremeMultiplier { get; set; } = 2.75;
    public int CorrectionGraceMs { get; set; } = 1200;
    public bool RejectUnauthorizedMovement { get; set; } = true;
    public bool ExemptAdmins { get; set; } = true;
    public bool ExemptMountedPlayers { get; set; } = true;
    public int MountGraceMs { get; set; } = 2000;
    public int EngineTeleportGraceMs { get; set; } = 5000;
    public bool TPRunesCompatibility { get; set; } = true;
    public int TPRunesGraceMs { get; set; } = 8000;
    public bool Th3EssentialsSpawnCompatibility { get; set; } = true;
    public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;

    public bool ReachEnabled { get; set; } = true;
    public double ReachTolerance { get; set; } = 1.25;
    public bool ReachLatencyCompensation { get; set; } = true;
    public double ReachMaxLatencyCompensation { get; set; } = 1.25;
    public bool ReachPositionHistoryEnabled { get; set; } = true;
    public int ReachPositionHistoryMs { get; set; } = 650;
    public int ReachMaxRewindMs { get; set; } = 350;
    public double ReachUseExtraTolerance { get; set; } = 0.20;
    public double ReachBreakExtraTolerance { get; set; } = 0.00;
    public int ReachNearThresholdStrikes { get; set; } = 3;
    public int ReachStrikeWindowMs { get; set; } = 3500;
    public int ReachStrikeDecayMs { get; set; } = 1800;
    public double ReachHardExcess { get; set; } = 1.5;
    public bool RejectOutOfReachActions { get; set; } = true;

    public bool MiningEnabled { get; set; } = true;
    public int VeinWindowMs { get; set; } = 2200;
    public int ConnectedBreakLimit { get; set; } = 18;
    public int MiningDuplicateTargetMs { get; set; } = 450;
    public bool RejectConnectedBreakBursts { get; set; } = true;
    public bool MiningCadenceValidationEnabled { get; set; } = false;
    public double MiningCadenceMinExpectedMs { get; set; } = 250;
    public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;
    public int MiningCadenceConfirmations { get; set; } = 3;
    public int MiningCadenceConfirmationWindowMs { get; set; } = 8000;
    public bool RejectImpossibleMiningCadence { get; set; } = false;

    public bool TpsAwarenessEnabled { get; set; } = true;
    public double TpsDegradedPercent { get; set; } = 85.0;
    public double TpsCriticalPercent { get; set; } = 60.0;
    public double TpsMaxTimingMultiplier { get; set; } = 2.5;
    public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;

    public bool PerFamilyRiskEnabled { get; set; } = true;
    public int MovementRiskDecaySeconds { get; set; } = 180;
    public int MovementRiskReviewThreshold { get; set; } = 25;
    public int MovementRiskSuspiciousThreshold { get; set; } = 50;
    public int ReachRiskDecaySeconds { get; set; } = 300;
    public int ReachRiskReviewThreshold { get; set; } = 25;
    public int ReachRiskSuspiciousThreshold { get; set; } = 50;
    public int MiningRiskDecaySeconds { get; set; } = 300;
    public int MiningRiskReviewThreshold { get; set; } = 25;
    public int MiningRiskSuspiciousThreshold { get; set; } = 50;
    public int PacketAbuseRiskDecaySeconds { get; set; } = 600;
    public int PacketAbuseRiskReviewThreshold { get; set; } = 25;
    public int PacketAbuseRiskSuspiciousThreshold { get; set; } = 50;
    public int ClientIntegrityRiskDecaySeconds { get; set; } = 1800;
    public int ClientIntegrityRiskReviewThreshold { get; set; } = 10;
    public int ClientIntegrityRiskSuspiciousThreshold { get; set; } = 50;

    public bool IncidentCorrelationEnabled { get; set; } = true;
    public int IncidentCorrelationWindowSeconds { get; set; } = 90;
    public int IncidentCorrelationRequiredCategories { get; set; } = 2;
    public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90;
    public int HistoryMaxRecords { get; set; } = 500;
}

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
    public List<AntiCheatRiskFamilyWebSnapshot> RiskFamilies { get; set; } = new();
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
    public List<AntiCheatFindingWebSnapshot> Findings { get; set; } = new();
    public List<AntiCheatModWebSnapshot> Mods { get; set; } = new();
    public List<AntiCheatAssemblyWebSnapshot> Assemblies { get; set; } = new();
    public List<AntiCheatRuntimeAssemblyWebSnapshot> RuntimeNewAssemblies { get; set; } = new();
    public List<AntiCheatScannerMetricWebSnapshot> ScannerMetrics { get; set; } = new();
    public List<AntiCheatConnectionDiagnosticWebSnapshot> RecentConnectionDiagnostics { get; set; } = new();
    public List<AntiCheatEvidenceCandidateWebSnapshot> EvidenceCandidates { get; set; } = new();
    public List<AntiCheatEvidencePackageWebSnapshot> CollectedEvidence { get; set; } = new();
}

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

public sealed class AntiCheatFindingWebSnapshot
{
    public string Scanner { get; set; } = "";
    public string Code { get; set; } = "";
    public int Severity { get; set; }
    public string Summary { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Category { get; set; } = "";
    public string EvidenceDomain { get; set; } = "Other";
    public bool ServerAuthoritative { get; set; }
    public int RiskPoints { get; set; }
    public long ObservedUtc { get; set; }
    public string Confidence { get; set; } = "Low";
    public int ConfidenceScore { get; set; }
    public int CorroboratingSignals { get; set; }
    public string Why { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
}

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

public sealed class AntiCheatConsequenceRuleWebSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string TriggerType { get; set; } = "FindingCode";
    public string TriggerValue { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Action { get; set; } = "Log";
    public string Duration { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class AntiCheatConsequenceSaveRequest
{
    public List<AntiCheatConsequenceRule> Rules { get; set; } = new();
    public bool ApplyCurrent { get; set; }
}

public sealed class AntiCheatAssemblyWebSnapshot
{
    public string AssemblyName { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string ModuleVersionId { get; set; } = "";
    public List<string> AssociatedModIds { get; set; } = new();
    public string StructureSha256 { get; set; } = "";
    public List<string> ModSystemTypeNames { get; set; } = new();
    public int StructureTokenCount { get; set; }
    public string BehaviorSha256 { get; set; } = "";
    public int BehaviorTokenCount { get; set; }
}

public sealed class AntiCheatForbiddenFingerprintWebSnapshot
{
    public string ModId { get; set; } = "";
    public bool HasPackageHash { get; set; }
    public bool HasContentHash { get; set; }
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public string Source { get; set; } = "";
    public string ResolvedVersion { get; set; } = "";
    public string ResolvedFileName { get; set; } = "";
    public long ResolvedUtc { get; set; }
    public int AssemblyNameCount { get; set; }
    public int AssemblyHashCount { get; set; }
    public int AssemblyStructureHashCount { get; set; }
    public int AssemblyBehaviorHashCount { get; set; }
    public int ReleaseCount { get; set; }
    public long ResolvedPackageBytes { get; set; }
    public int ResolvedDllCount { get; set; }
    public long ResolvedDllBytes { get; set; }
    public List<string> ResolvedVersions { get; set; } = new();
    public int SimilarityAssemblyCount { get; set; }
    public int ManualEntryCount { get; set; }
    public List<AntiCheatManualFingerprintWebSnapshot> ManualEntries { get; set; } = new();
}

public sealed class AntiCheatManualFingerprintWebSnapshot
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public List<string> AssemblySha256s { get; set; } = new();
    public List<string> AssemblyMvids { get; set; } = new();
    public List<string> AssemblyStructureSha256s { get; set; } = new();
    public List<string> AssemblyBehaviorSha256s { get; set; } = new();
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

public sealed class AntiCheatManualFingerprintSaveRequest
{
    public string Action { get; set; } = "save";
    public string ModId { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public List<string> AssemblySha256s { get; set; } = new();
    public List<string> AssemblyMvids { get; set; } = new();
    public List<string> AssemblyStructureSha256s { get; set; } = new();
    public List<string> AssemblyBehaviorSha256s { get; set; } = new();
}

public sealed class AntiCheatRuntimeAssemblyWebSnapshot
{
    public string AssemblyName { get; set; } = "";
    public string Version { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ModuleVersionId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool IsDynamic { get; set; }
    public string LoadContextName { get; set; } = "";
    public bool AssociatedWithLoadedMod { get; set; }
}

public sealed class AntiCheatScannerMetricWebSnapshot
{
    public string Scanner { get; set; } = "";
    public long DurationMs { get; set; }
    public bool Success { get; set; }
    public int ModCount { get; set; }
    public int AssemblyCount { get; set; }
    public int FindingCount { get; set; }
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
}



public sealed class AntiCheatPacketGuardWebSnapshot
{
    public int ConfirmedSpeedRepeatRisk { get; set; }
    public bool KickRepeatedConfirmedSpeed { get; set; }
    public bool MovementModeLimitsEnabled { get; set; }
    public double MaxHorizontalSpeed { get; set; }
    public double GroundMaxHorizontalSpeed { get; set; }
    public double SwimmingMaxHorizontalSpeed { get; set; }
    public double AirborneMaxHorizontalSpeed { get; set; }
    public bool MountProfilesEnabled { get; set; }
    public bool MountProfileEnforcementEnabled { get; set; }
    public bool UnknownMountEnforcementEnabled { get; set; }
    public int MountProfileConfirmSamples { get; set; }
    public int MountProfileConfirmWindowMs { get; set; }
    public bool MountRiderCorrelationEnabled { get; set; }
    public double DraconisMaxHorizontalSpeed { get; set; }
    public double DraconisMaxVerticalSpeed { get; set; }
    public double DraconisMaxAcceleration { get; set; }
    public double DraconisMaxRiderDistance { get; set; }
    public double DraconisMaxRiderRelativeSpeed { get; set; }
    public double ShipwrightMaxHorizontalSpeed { get; set; }
    public double ShipwrightMaxVerticalSpeed { get; set; }
    public double ShipwrightMaxAcceleration { get; set; }
    public double ShipwrightMaxRiderDistance { get; set; }
    public double ShipwrightMaxRiderRelativeSpeed { get; set; }
    public double UnknownMountMaxHorizontalSpeed { get; set; }
    public double UnknownMountMaxVerticalSpeed { get; set; }
    public double UnknownMountMaxAcceleration { get; set; }
    public double UnknownMountMaxRiderDistance { get; set; }
    public double UnknownMountMaxRiderRelativeSpeed { get; set; }
    public bool Enabled { get; set; }
    public bool IngressEnabled { get; set; }
    public bool IngressActive { get; set; }
    public string IngressStatus { get; set; } = "Disabled";
    public int IngressMaxPacketKiB { get; set; }
    public int MaxViewDistanceBlocks { get; set; }
    public int IngressPacketsPerSecond { get; set; }
    public int IngressPacketBurst { get; set; }
    public int BlockPacketsPerSecond { get; set; }
    public int BlockPacketBurst { get; set; }
    public int SettingsPacketsPerSecond { get; set; }
    public int SettingsPacketBurst { get; set; }
    public bool PlayerWarningEnabled { get; set; }
    public int PlayerWarningCooldownSeconds { get; set; }
    public int AccelerationSampleMinMs { get; set; }
    public double AccelerationSmoothingFactor { get; set; }
    public bool NoclipEnabled { get; set; }
    public int NoclipRequiredCollisionSamples { get; set; }
    public double NoclipMinPenetrationDepth { get; set; }
    public double NoclipEndpointMinNetMovement { get; set; }
    public double MountAccelerationMinSpeedRatio { get; set; }
    public bool DiagnosticContextEnabled { get; set; }
    public bool DiagnosticSuppressionEnabled { get; set; }
    public int DiagnosticSpeedHistorySamples { get; set; }
    public bool MovementEnabled { get; set; }
    public int MovementSampleMs { get; set; }
    public double HardMoveDistance { get; set; }
    public int MovementConfirmSamples { get; set; }
    public int MovementConfirmWindowMs { get; set; }
    public double MovementExtremeMultiplier { get; set; }
    public int CorrectionGraceMs { get; set; }
    public bool RejectUnauthorizedMovement { get; set; }
    public bool ExemptAdmins { get; set; }
    public bool ExemptMountedPlayers { get; set; }
    public int MountGraceMs { get; set; }
    public int EngineTeleportGraceMs { get; set; }
    public bool TPRunesCompatibility { get; set; }
    public int TPRunesGraceMs { get; set; }
    public bool TPRunesDetected { get; set; }
    public bool Th3EssentialsSpawnCompatibility { get; set; }
    public int Th3EssentialsSpawnGraceMs { get; set; }
    public bool Th3EssentialsDetected { get; set; }
    public bool ReachEnabled { get; set; }
    public double ReachTolerance { get; set; }
    public bool ReachLatencyCompensation { get; set; }
    public double ReachMaxLatencyCompensation { get; set; }
    public bool ReachPositionHistoryEnabled { get; set; }
    public int ReachPositionHistoryMs { get; set; }
    public int ReachMaxRewindMs { get; set; }
    public double ReachUseExtraTolerance { get; set; }
    public double ReachBreakExtraTolerance { get; set; }
    public int ReachNearThresholdStrikes { get; set; }
    public int ReachStrikeWindowMs { get; set; }
    public int ReachStrikeDecayMs { get; set; }
    public double ReachHardExcess { get; set; }
    public bool RejectOutOfReachActions { get; set; }
    public bool MiningEnabled { get; set; }
    public int VeinWindowMs { get; set; }
    public int ConnectedBreakLimit { get; set; }
    public int MiningDuplicateTargetMs { get; set; }
    public bool RejectConnectedBreakBursts { get; set; }
    public bool MiningCadenceValidationEnabled { get; set; }
    public double MiningCadenceMinExpectedMs { get; set; }
    public double MiningCadenceSuspiciousRatio { get; set; }
    public int MiningCadenceConfirmations { get; set; }
    public int MiningCadenceConfirmationWindowMs { get; set; }
    public bool RejectImpossibleMiningCadence { get; set; }
    public bool TpsAwarenessEnabled { get; set; }
    public double TpsDegradedPercent { get; set; }
    public double TpsCriticalPercent { get; set; }
    public double TpsMaxTimingMultiplier { get; set; }
    public bool TpsSuppressTimingRejectsWhenCritical { get; set; }
    public bool TpsReady { get; set; }
    public double TpsTarget { get; set; }
    public double TpsEstimated { get; set; }
    public double TpsHealthPercent { get; set; }
    public double TpsTimingMultiplier { get; set; }
    public string TpsBand { get; set; } = "Disabled";
    public int TpsSampleCount { get; set; }
    public bool PerFamilyRiskEnabled { get; set; }
    public int MovementRiskDecaySeconds { get; set; }
    public int MovementRiskReviewThreshold { get; set; }
    public int MovementRiskSuspiciousThreshold { get; set; }
    public int ReachRiskDecaySeconds { get; set; }
    public int ReachRiskReviewThreshold { get; set; }
    public int ReachRiskSuspiciousThreshold { get; set; }
    public int MiningRiskDecaySeconds { get; set; }
    public int MiningRiskReviewThreshold { get; set; }
    public int MiningRiskSuspiciousThreshold { get; set; }
    public int PacketAbuseRiskDecaySeconds { get; set; }
    public int PacketAbuseRiskReviewThreshold { get; set; }
    public int PacketAbuseRiskSuspiciousThreshold { get; set; }
    public int ClientIntegrityRiskDecaySeconds { get; set; }
    public int ClientIntegrityRiskReviewThreshold { get; set; }
    public int ClientIntegrityRiskSuspiciousThreshold { get; set; }
    public bool IncidentCorrelationEnabled { get; set; }
    public int IncidentCorrelationWindowSeconds { get; set; }
    public int IncidentCorrelationRequiredCategories { get; set; }
    public int IncidentCorrelationMaxRiskBonus { get; set; }
    public List<AntiCheatTrustedTeleportSourceWebSnapshot> TrustedTeleportSources { get; set; } = new();
    public List<AntiCheatPacketSuppressionWebSnapshot> PacketSuppressions { get; set; } = new();
}

public sealed class AntiCheatTrustedTeleportSourceWebSnapshot
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string TrustScope { get; set; } = "Movement + Reach";
    public string Verification { get; set; } = "";
    public long ObservedCount { get; set; }
    public long LastSeenUtc { get; set; }
    public string LastPlayerName { get; set; } = "";
    public string LastReason { get; set; } = "";
    public List<AntiCheatTrustedTeleportEventWebSnapshot> RecentEvents { get; set; } = new();
}

public sealed class AntiCheatTrustedTeleportEventWebSnapshot
{
    public long Utc { get; set; }
    public string PlayerName { get; set; } = "";
    public string PlayerUid { get; set; } = "";
    public double FromX { get; set; }
    public double FromY { get; set; }
    public double FromZ { get; set; }
    public int FromDimension { get; set; }
    public double ToX { get; set; }
    public double ToY { get; set; }
    public double ToZ { get; set; }
    public int ToDimension { get; set; }
    public string Reason { get; set; } = "";
    public string Verification { get; set; } = "";
}

public sealed class AntiCheatPacketSuppressionWebSnapshot
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Safety";
    public string Description { get; set; } = "";
    public long Count { get; set; }
    public long LastSeenUtc { get; set; }
    public string LastPlayerName { get; set; } = "";
    public string LastDetail { get; set; } = "";
}

public sealed class AntiCheatPacketGuardSaveRequest
{
    public bool? IngressEnabled { get; set; }
    // Nullable request values preserve settings for older web/Shift+L clients.
    public int? ConfirmedSpeedRepeatRisk { get; set; }
    public bool? KickRepeatedConfirmedSpeed { get; set; }
    public bool? MovementModeLimitsEnabled { get; set; }
    public double? MaxHorizontalSpeed { get; set; }
    public double? GroundMaxHorizontalSpeed { get; set; }
    public double? SwimmingMaxHorizontalSpeed { get; set; }
    public double? AirborneMaxHorizontalSpeed { get; set; }
    public bool? MountProfilesEnabled { get; set; }
    public bool? MountProfileEnforcementEnabled { get; set; }
    public bool? UnknownMountEnforcementEnabled { get; set; }
    public int? MountProfileConfirmSamples { get; set; }
    public int? MountProfileConfirmWindowMs { get; set; }
    public bool? MountRiderCorrelationEnabled { get; set; }
    public double? DraconisMaxHorizontalSpeed { get; set; }
    public double? DraconisMaxVerticalSpeed { get; set; }
    public double? DraconisMaxAcceleration { get; set; }
    public double? DraconisMaxRiderDistance { get; set; }
    public double? DraconisMaxRiderRelativeSpeed { get; set; }
    public double? ShipwrightMaxHorizontalSpeed { get; set; }
    public double? ShipwrightMaxVerticalSpeed { get; set; }
    public double? ShipwrightMaxAcceleration { get; set; }
    public double? ShipwrightMaxRiderDistance { get; set; }
    public double? ShipwrightMaxRiderRelativeSpeed { get; set; }
    public double? UnknownMountMaxHorizontalSpeed { get; set; }
    public double? UnknownMountMaxVerticalSpeed { get; set; }
    public double? UnknownMountMaxAcceleration { get; set; }
    public double? UnknownMountMaxRiderDistance { get; set; }
    public double? UnknownMountMaxRiderRelativeSpeed { get; set; }
    public bool Enabled { get; set; } = true;
    public bool PlayerWarningEnabled { get; set; } = false;
    public int PlayerWarningCooldownSeconds { get; set; } = 15;
    public int AccelerationSampleMinMs { get; set; } = 220;
    public double AccelerationSmoothingFactor { get; set; } = 0.25;
    public bool NoclipEnabled { get; set; } = false;
    public int NoclipRequiredCollisionSamples { get; set; } = 3;
    public double NoclipMinPenetrationDepth { get; set; } = 0.18;
    public double NoclipEndpointMinNetMovement { get; set; } = 0.45;
    public double MountAccelerationMinSpeedRatio { get; set; } = 0.85;
    public bool DiagnosticContextEnabled { get; set; } = true;
    public bool DiagnosticSuppressionEnabled { get; set; } = true;
    public int DiagnosticSpeedHistorySamples { get; set; } = 8;
    public bool MovementEnabled { get; set; } = true;
    public int MovementSampleMs { get; set; } = 75;
    public double HardMoveDistance { get; set; } = 32.0;
    public int MovementConfirmSamples { get; set; } = 2;
    public int MovementConfirmWindowMs { get; set; } = 600;
    public double MovementExtremeMultiplier { get; set; } = 2.75;
    public int CorrectionGraceMs { get; set; } = 1200;
    public bool RejectUnauthorizedMovement { get; set; } = true;
    public bool ExemptAdmins { get; set; } = true;
    public bool ExemptMountedPlayers { get; set; } = true;
    public int MountGraceMs { get; set; } = 2000;
    public int EngineTeleportGraceMs { get; set; } = 5000;
    public bool TPRunesCompatibility { get; set; } = true;
    public int TPRunesGraceMs { get; set; } = 8000;
    public bool Th3EssentialsSpawnCompatibility { get; set; } = true;
    public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;
    public bool ReachEnabled { get; set; } = true;
    public double ReachTolerance { get; set; } = 1.25;
    public bool ReachLatencyCompensation { get; set; } = true;
    public double ReachMaxLatencyCompensation { get; set; } = 1.25;
    public bool ReachPositionHistoryEnabled { get; set; } = true;
    public int ReachPositionHistoryMs { get; set; } = 650;
    public int ReachMaxRewindMs { get; set; } = 350;
    public double ReachUseExtraTolerance { get; set; } = 0.20;
    public double ReachBreakExtraTolerance { get; set; } = 0.00;
    public int ReachNearThresholdStrikes { get; set; } = 3;
    public int ReachStrikeWindowMs { get; set; } = 3500;
    public int ReachStrikeDecayMs { get; set; } = 1800;
    public double ReachHardExcess { get; set; } = 1.5;
    public bool RejectOutOfReachActions { get; set; } = true;
    public bool MiningEnabled { get; set; } = true;
    public int VeinWindowMs { get; set; } = 2200;
    public int ConnectedBreakLimit { get; set; } = 18;
    public int MiningDuplicateTargetMs { get; set; } = 450;
    public bool RejectConnectedBreakBursts { get; set; } = true;
    public bool MiningCadenceValidationEnabled { get; set; } = false;
    public double MiningCadenceMinExpectedMs { get; set; } = 250;
    public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;
    public int MiningCadenceConfirmations { get; set; } = 3;
    public int MiningCadenceConfirmationWindowMs { get; set; } = 8000;
    public bool RejectImpossibleMiningCadence { get; set; } = false;
    public bool TpsAwarenessEnabled { get; set; } = true;
    public double TpsDegradedPercent { get; set; } = 85.0;
    public double TpsCriticalPercent { get; set; } = 60.0;
    public double TpsMaxTimingMultiplier { get; set; } = 2.5;
    public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;
    public bool PerFamilyRiskEnabled { get; set; } = true;
    public int MovementRiskDecaySeconds { get; set; } = 180;
    public int MovementRiskReviewThreshold { get; set; } = 25;
    public int MovementRiskSuspiciousThreshold { get; set; } = 50;
    public int ReachRiskDecaySeconds { get; set; } = 300;
    public int ReachRiskReviewThreshold { get; set; } = 25;
    public int ReachRiskSuspiciousThreshold { get; set; } = 50;
    public int MiningRiskDecaySeconds { get; set; } = 300;
    public int MiningRiskReviewThreshold { get; set; } = 25;
    public int MiningRiskSuspiciousThreshold { get; set; } = 50;
    public int PacketAbuseRiskDecaySeconds { get; set; } = 600;
    public int PacketAbuseRiskReviewThreshold { get; set; } = 25;
    public int PacketAbuseRiskSuspiciousThreshold { get; set; } = 50;
    public int ClientIntegrityRiskDecaySeconds { get; set; } = 1800;
    public int ClientIntegrityRiskReviewThreshold { get; set; } = 10;
    public int ClientIntegrityRiskSuspiciousThreshold { get; set; } = 50;
    public bool IncidentCorrelationEnabled { get; set; } = true;
    public int IncidentCorrelationWindowSeconds { get; set; } = 90;
    public int IncidentCorrelationRequiredCategories { get; set; } = 2;
    public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90;
    public int HistoryMaxRecords { get; set; } = 500;
}

public class AntiCheatHistorySummaryWebSnapshot
{
    public bool Enabled { get; set; }
    public int RetentionDays { get; set; }
    public int MaxRecords { get; set; }
    public int TotalRecords { get; set; }
    public int UniquePlayers { get; set; }
    public int Last24Hours { get; set; }
}

public sealed class AntiCheatHistoryWebSnapshot : AntiCheatHistorySummaryWebSnapshot
{
    public bool StorageReady { get; set; }
    public string StorageError { get; set; } = "";
    public string PlayerFilter { get; set; } = "";
    public string FindingFilter { get; set; } = "";
    public List<string> FindingOptions { get; set; } = new();
    public List<AntiCheatIncidentWebSnapshot> Incidents { get; set; } = new();
}

public sealed class AntiCheatIncidentWebSnapshot
{
    public long Id { get; set; }
    public long UnixMilliseconds { get; set; }
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Scanner { get; set; } = "";
    public string Code { get; set; } = "";
    public int Severity { get; set; }
    public string Summary { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Evidence { get; set; } = "";
    public int RiskBefore { get; set; }
    public int RiskAfter { get; set; }
    public string StatusAfter { get; set; } = "";
    public string Consequence { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int Dimension { get; set; }
    public string Category { get; set; } = "";
    public string Confidence { get; set; } = "Low";
    public int ConfidenceScore { get; set; }
    public int CorroboratingSignals { get; set; }
    public string Why { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
}

public sealed class AntiCheatBlacklistProfileWebSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int ModCount { get; set; }
    public int FingerprintCount { get; set; }
    public List<string> ModIds { get; set; } = new();
    public long CreatedUtc { get; set; }
    public long UpdatedUtc { get; set; }
}

public sealed class AntiCheatFullConfigSaveRequest
{
    public string Json { get; set; } = "";
}
