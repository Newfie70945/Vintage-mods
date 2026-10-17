using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace BlockLogger.AntiCheat;

public sealed class AntiCheatConfig
{
	public const int CurrentSchemaVersion = 62;

	public static readonly string[] DefaultForbiddenModIds = new string[19]
	{
		"prosping", "xrayblock", "blocksoverlay", "amfinder", "orewhisper", "prospectingscan", "gimap", "birdseye", "subtleparticlesplus", "nf3ttranslocatorfindercmd",
		"findanyblock", "modmenu", "autoattack", "clayformerprescko", "freeroamcam", "mobfinder", "smithingmacrodebug", "stepuptwoblocks", "zinkmodautoclayupdate"
	};

	public int SchemaVersion { get; set; } = 62;

	public bool OreShieldEnabled { get; set; } = false;

	public bool OreShieldProtectModdedOres { get; set; } = true;

	public bool OreShieldRevealOnUnknownNeighbor { get; set; } = false;

	public bool OreShieldStrictOreMatching { get; set; } = true;

	public int OreShieldRevealRadiusBlocks { get; set; } = 0;

	public bool OreShieldRevealWholeVeinOnMine { get; set; } = false;

	public int OreShieldDiscoveryRangeBlocks { get; set; } = 96;

	public int OreShieldVisibilityScanIntervalMs { get; set; } = 250;

	public int OreShieldMaxLosChecksPerPass { get; set; } = 96;

	public int OreShieldViewConeDegrees { get; set; } = 140;

	public int OreShieldIdleRescanMs { get; set; } = 3000;

	public bool OreShieldLeakAuditEnabled { get; set; } = true;

	public int OreShieldMaxVeinRevealBlocks { get; set; } = 2048;

	public bool OreShieldChunkCacheEnabled { get; set; } = true;

	public bool OreShieldDecoysEnabled { get; set; } = false;

	public int OreShieldDecoyPercent { get; set; } = 0;

	public int OreShieldDecoyClusterSize { get; set; } = 2;

	public int OreShieldChunkCacheMiB { get; set; } = 96;

	public int OreShieldMaxCachedChunks { get; set; } = 4096;

	public List<string> OreShieldAdditionalOrePatterns { get; set; } = new List<string>();

	public List<string> OreShieldExcludedBlockPatterns { get; set; } = new List<string>();

	[JsonProperty("OreGuardEnabled")]
	private bool LegacyOreGuardEnabled
	{
		set
		{
			OreShieldEnabled = value;
		}
	}

	[JsonProperty("OreGuardProtectModdedOres")]
	private bool LegacyOreGuardProtectModdedOres
	{
		set
		{
			OreShieldProtectModdedOres = value;
		}
	}

	[JsonProperty("OreGuardDecoysEnabled")]
	private bool LegacyOreGuardDecoysEnabled
	{
		set
		{
			OreShieldDecoysEnabled = value;
		}
	}

	[JsonProperty("OreGuardDecoyPercent")]
	private int LegacyOreGuardDecoyPercent
	{
		set
		{
			OreShieldDecoyPercent = value;
		}
	}

	[JsonProperty("OreGuardDecoyClusterSize")]
	private int LegacyOreGuardDecoyClusterSize
	{
		set
		{
			OreShieldDecoyClusterSize = value;
		}
	}

	[JsonProperty("OreGuardChunkCacheMiB")]
	private int LegacyOreGuardChunkCacheMiB
	{
		set
		{
			OreShieldChunkCacheMiB = value;
		}
	}

	[JsonProperty("OreGuardMaxCachedChunks")]
	private int LegacyOreGuardMaxCachedChunks
	{
		set
		{
			OreShieldMaxCachedChunks = value;
		}
	}

	[JsonProperty("OreGuardAdditionalOrePatterns")]
	private List<string>? LegacyOreGuardAdditionalOrePatterns
	{
		set
		{
			if (value != null)
			{
				OreShieldAdditionalOrePatterns = value;
			}
		}
	}

	[JsonProperty("OreGuardExcludedBlockPatterns")]
	private List<string>? LegacyOreGuardExcludedBlockPatterns
	{
		set
		{
			if (value != null)
			{
				OreShieldExcludedBlockPatterns = value;
			}
		}
	}

	public bool PacketGuardEnabled { get; set; } = true;

	public bool PacketGuardIngressEnabled { get; set; } = true;

	public int PacketGuardIngressMaxPacketKiB { get; set; } = 2048;

	public int PacketGuardMaxViewDistanceBlocks { get; set; } = 1024;

	public int PacketGuardIngressPacketsPerSecond { get; set; } = 300;

	public int PacketGuardIngressPacketBurst { get; set; } = 900;

	public int PacketGuardBlockPacketsPerSecond { get; set; } = 60;

	public int PacketGuardBlockPacketBurst { get; set; } = 180;

	public int PacketGuardSettingsPacketsPerSecond { get; set; } = 2;

	public int PacketGuardSettingsPacketBurst { get; set; } = 8;

	public int PacketGuardIngressFindingCooldownSeconds { get; set; } = 10;

	public bool PacketGuardPlayerWarningEnabled { get; set; } = false;

	public int PacketGuardPlayerWarningCooldownSeconds { get; set; } = 15;

	public bool PacketGuardMovementEnabled { get; set; } = true;

	public bool PacketGuardFlightReviewEnabled { get; set; } = true;

	public int PacketGuardFlightReviewConfirmSeconds { get; set; } = 6;

	public int PacketGuardFlightReviewSampleMs { get; set; } = 250;

	public int PacketGuardMovementSampleMs { get; set; } = 75;

	public double PacketGuardHardMoveDistance { get; set; } = 8.0;

	public double PacketGuardMovementMaxSampleScale { get; set; } = 4.0;

	public int PacketGuardMovementConfirmSamples { get; set; } = 2;

	public int PacketGuardMovementConfirmWindowMs { get; set; } = 600;

	public double PacketGuardMovementExtremeMultiplier { get; set; } = 2.75;

	public int PacketGuardCorrectionGraceMs { get; set; } = 1200;

	public bool PacketGuardRejectUnauthorizedMovement { get; set; } = true;

	public bool PacketGuardUnverifiedTeleportReconciliationEnabled { get; set; } = false;

	public double PacketGuardUnverifiedTeleportMinDistance { get; set; } = 128.0;

	public int PacketGuardUnverifiedTeleportRepeatWindowMs { get; set; } = 30000;

	public int PacketGuardUnverifiedTeleportRepeatCount { get; set; } = 3;

	public int PacketGuardConfirmedSpeedRepeatRisk { get; set; } = 25;

	public bool PacketGuardKickRepeatedConfirmedSpeed { get; set; } = true;

	public bool PacketGuardSpeedHackEnabled { get; set; } = true;

	public double PacketGuardMaxHorizontalSpeed { get; set; } = 30.0;

	public bool PacketGuardMovementModeLimitsEnabled { get; set; } = true;

	public double PacketGuardGroundMaxHorizontalSpeed { get; set; } = 24.0;

	public double PacketGuardSwimmingMaxHorizontalSpeed { get; set; } = 14.0;

	public double PacketGuardAirborneMaxHorizontalSpeed { get; set; } = 22.0;

	public int PacketGuardSpeedWindowMs { get; set; } = 750;

	public int PacketGuardSpeedConfirmWindows { get; set; } = 2;

	public bool PacketGuardRejectSpeedHack { get; set; } = true;

	public bool PacketGuardAccelerationEnabled { get; set; } = true;

	public double PacketGuardAccelerationMinSpeed { get; set; } = 16.0;

	public double PacketGuardMaxHorizontalAcceleration { get; set; } = 120.0;

	public int PacketGuardAccelerationConfirmSamples { get; set; } = 2;

	public int PacketGuardAccelerationConfirmWindowMs { get; set; } = 900;

	public int PacketGuardAccelerationSampleMinMs { get; set; } = 220;

	public double PacketGuardAccelerationSmoothingFactor { get; set; } = 0.25;

	public bool PacketGuardRejectAccelerationHack { get; set; } = true;

	public bool PacketGuardNoclipEnabled { get; set; } = false;

	public double PacketGuardNoclipMinPathDistance { get; set; } = 1.25;

	public double PacketGuardNoclipSampleStep { get; set; } = 0.3;

	public int PacketGuardNoclipRequiredCollisionSamples { get; set; } = 3;

	public double PacketGuardNoclipMinPenetrationDepth { get; set; } = 0.18;

	public bool PacketGuardNoclipEndpointEnabled { get; set; } = true;

	public double PacketGuardNoclipEndpointMinPenetrationDepth { get; set; } = 0.18;

	public int PacketGuardNoclipEndpointConfirmSamples { get; set; } = 3;

	public double PacketGuardNoclipEndpointMinNetMovement { get; set; } = 0.45;

	public bool PacketGuardRejectNoclip { get; set; } = true;

	public int PacketGuardSafePositionHistorySize { get; set; } = 24;

	public int PacketGuardSafePositionMaxAgeMs { get; set; } = 10000;

	public int PacketGuardRollbackSafetyLagMs { get; set; } = 300;

	public bool PacketGuardRollbackValidateCollision { get; set; } = true;

	public int PacketGuardRollbackValidationCandidates { get; set; } = 12;

	public int PacketGuardRollbackAntiLoopWindowMs { get; set; } = 2500;

	public double PacketGuardRollbackAntiLoopRisk { get; set; } = 20.0;

	public int PacketGuardMovementDuplicateFindingCooldownMs { get; set; } = 1200;

	public bool PacketGuardMovementRiskEnabled { get; set; } = true;

	public double PacketGuardMovementRiskDecayPerSecond { get; set; } = 2.0;

	public double PacketGuardMovementRiskAcceleration { get; set; } = 18.0;

	public double PacketGuardMovementRiskSpeed { get; set; } = 30.0;

	public double PacketGuardMovementRiskNoclip { get; set; } = 45.0;

	public double PacketGuardMovementRiskHardMove { get; set; } = 30.0;

	public double PacketGuardMovementRiskMount { get; set; } = 35.0;

	public int PacketGuardMovementConfidenceCorrelationWindowMs { get; set; } = 2500;

	public double PacketGuardMovementConfidenceCorrelationBonus { get; set; } = 12.0;

	public double PacketGuardMovementConfidenceGuardedThreshold { get; set; } = 25.0;

	public double PacketGuardMovementConfidenceHighThreshold { get; set; } = 50.0;

	public double PacketGuardMovementConfidenceCriticalThreshold { get; set; } = 75.0;

	public bool PacketGuardMovementConfidenceEscalatesSeverity { get; set; } = true;

	public bool PacketGuardExemptAdmins { get; set; } = true;

	public bool PacketGuardExemptMountedPlayers { get; set; } = true;

	public int PacketGuardMountGraceMs { get; set; } = 2000;

	public bool PacketGuardMountProfilesEnabled { get; set; } = true;

	public bool PacketGuardMountProfileEnforcementEnabled { get; set; } = true;

	public bool PacketGuardUnknownMountEnforcementEnabled { get; set; } = false;

	public int PacketGuardMountProfileConfirmSamples { get; set; } = 3;

	public int PacketGuardMountProfileConfirmWindowMs { get; set; } = 1200;

	public double PacketGuardShipwrightMaxHorizontalSpeed { get; set; } = 55.0;

	public double PacketGuardShipwrightMaxVerticalSpeed { get; set; } = 20.0;

	public double PacketGuardShipwrightMaxAcceleration { get; set; } = 300.0;

	public double PacketGuardShipwrightMaxRiderDistance { get; set; } = 20.0;

	public double PacketGuardDraconisMaxHorizontalSpeed { get; set; } = 80.0;

	public double PacketGuardDraconisMaxVerticalSpeed { get; set; } = 50.0;

	public double PacketGuardDraconisMaxAcceleration { get; set; } = 420.0;

	public double PacketGuardDraconisMaxRiderDistance { get; set; } = 12.0;

	public double PacketGuardUnknownMountMaxHorizontalSpeed { get; set; } = 40.0;

	public double PacketGuardUnknownMountMaxVerticalSpeed { get; set; } = 40.0;

	public double PacketGuardUnknownMountMaxAcceleration { get; set; } = 350.0;

	public double PacketGuardMountAccelerationMinSpeedRatio { get; set; } = 0.85;

	public bool PacketGuardDiagnosticContextEnabled { get; set; } = true;

	public bool PacketGuardDiagnosticSuppressionEnabled { get; set; } = true;

	public int PacketGuardDiagnosticSpeedHistorySamples { get; set; } = 8;

	public double PacketGuardUnknownMountMaxRiderDistance { get; set; } = 16.0;

	public bool PacketGuardMountRiderCorrelationEnabled { get; set; } = true;

	public double PacketGuardShipwrightMaxRiderRelativeSpeed { get; set; } = 24.0;

	public double PacketGuardDraconisMaxRiderRelativeSpeed { get; set; } = 20.0;

	public double PacketGuardUnknownMountMaxRiderRelativeSpeed { get; set; } = 24.0;

	public int PacketGuardEngineTeleportGraceMs { get; set; } = 5000;

	public bool PacketGuardTPRunesCompatibility { get; set; } = true;

	public int PacketGuardTPRunesGraceMs { get; set; } = 8000;

	public bool PacketGuardTh3EssentialsSpawnCompatibility { get; set; } = true;

	public int PacketGuardTh3EssentialsSpawnGraceMs { get; set; } = 8000;

	public bool PacketGuardReachEnabled { get; set; } = true;

	public double PacketGuardReachTolerance { get; set; } = 1.25;

	public bool PacketGuardReachLatencyCompensation { get; set; } = true;

	public double PacketGuardReachMaxLatencyCompensation { get; set; } = 1.25;

	public bool PacketGuardReachPositionHistoryEnabled { get; set; } = true;

	public int PacketGuardReachPositionHistoryMs { get; set; } = 650;

	public int PacketGuardReachMaxRewindMs { get; set; } = 350;

	public double PacketGuardReachUseExtraTolerance { get; set; } = 0.2;

	public double PacketGuardReachBreakExtraTolerance { get; set; } = 0.0;

	public int PacketGuardReachNearThresholdStrikes { get; set; } = 3;

	public int PacketGuardReachStrikeWindowMs { get; set; } = 3500;

	public int PacketGuardReachStrikeDecayMs { get; set; } = 1800;

	public double PacketGuardReachHardExcess { get; set; } = 1.5;

	public int PacketGuardReachDistinctTargetsRequired { get; set; } = 2;

	public bool PacketGuardRejectOutOfReachActions { get; set; } = true;

	public bool PacketGuardMiningEnabled { get; set; } = true;

	public int PacketGuardVeinWindowMs { get; set; } = 2200;

	public int PacketGuardConnectedBreakLimit { get; set; } = 18;

	public int PacketGuardConnectedBreakConfirmations { get; set; } = 2;

	public double PacketGuardConnectedBreakExtremeMultiplier { get; set; } = 1.75;

	public int PacketGuardMiningDuplicateTargetMs { get; set; } = 450;

	public bool PacketGuardRejectConnectedBreakBursts { get; set; } = true;

	public bool PacketGuardMiningCadenceValidationEnabled { get; set; } = false;

	public double PacketGuardMiningCadenceMinExpectedMs { get; set; } = 250.0;

	public double PacketGuardMiningCadenceSuspiciousRatio { get; set; } = 0.38;

	public int PacketGuardMiningCadenceConfirmations { get; set; } = 3;

	public int PacketGuardMiningCadenceConfirmationWindowMs { get; set; } = 8000;

	public bool PacketGuardRejectImpossibleMiningCadence { get; set; } = false;

	public bool PacketGuardTpsAwarenessEnabled { get; set; } = true;

	public double PacketGuardTpsDegradedPercent { get; set; } = 85.0;

	public double PacketGuardTpsCriticalPercent { get; set; } = 60.0;

	public double PacketGuardTpsMaxTimingMultiplier { get; set; } = 2.5;

	public bool PacketGuardTpsSuppressTimingRejectsWhenCritical { get; set; } = true;

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

	public bool Enabled { get; set; } = true;

	public bool RequireClient { get; set; } = true;

	public bool ModDbAutomaticConsequencesEnabled { get; set; } = false;

	public bool ClientModDbVerificationEnabled { get; set; } = true;

	public bool StrictClientModDbEnforcementEnabled { get; set; } = true;

	public bool StartupIntegrityGateEnabled { get; set; } = true;

	public bool StartupIntegrityGateBlockWorldInteractions { get; set; } = false;

	public bool StartupIntegrityGateRequireClean { get; set; } = false;

	public bool StartupIntegrityGateDisconnectOnFailure { get; set; } = true;

	public int StartupIntegrityGateNoticeCooldownSeconds { get; set; } = 4;

	public bool StartupCoreAssemblyInspectionEnabled { get; set; } = true;

	public List<string> StartupCoreAssemblyNames { get; set; } = new List<string> { "VintagestoryAPI" };

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

	public bool RequireApprovedClientBuild { get; set; } = false;

	public bool RequireHardenedClientCapabilities { get; set; } = false;

	public bool RequireCurrentClientRelease { get; set; } = false;

	public bool RandomizedSelfAttestationEnabled { get; set; } = true;

	public int SelfAttestationMethodProbeCount { get; set; } = 6;

	public int SelfAttestationTypeProbeCount { get; set; } = 3;

	public int SelfAttestationFailuresBeforeKick { get; set; } = 3;

	public bool RequireSignedClientBuildManifest { get; set; } = false;

	public string ClientBuildManifestPublicKeyBase64 { get; set; } = "";

	public bool SimilarityDetectionEnabled { get; set; } = true;

	public int SimilarityReviewThresholdPercent { get; set; } = 72;

	public int SimilarityHighThresholdPercent { get; set; } = 88;

	public bool RuntimeSurfaceInspectionEnabled { get; set; } = true;

	public bool SuspiciousHotkeyInspectionEnabled { get; set; } = true;

	public bool SuspiciousClientCommandInspectionEnabled { get; set; } = true;

	public List<string> SuspiciousRuntimeTerms { get; set; } = new List<string>
	{
		"xray", "x-ray", "esp", "freecam", "free camera", "noclip", "no clip", "killaura", "kill aura", "aimbot",
		"autoclick", "auto click", "speedhack", "speed hack", "flyhack", "fly hack", "fullbright", "full bright", "wallhack", "wall hack",
		"tracer", "reachhack", "reach hack", "orefinder", "ore finder", "mobfinder", "mob finder", "entityesp", "playeresp"
	};

	public bool CanonicalServerModIntegrityEnabled { get; set; } = true;

	public bool SuspectPackageCollectionEnabled { get; set; } = true;

	public int SuspectPackageCollectionMaxMb { get; set; } = 96;

	public int SuspectPackageCollectionTimeoutSeconds { get; set; } = 120;

	public string SuspectPackageDeclineAction { get; set; } = "TempBan";

	public int SuspectPackageDeclineTempBanHours { get; set; } = 24;

	public int SuspectPackageUploadKbps { get; set; } = 384;

	public bool ProtectedAssetInspectionEnabled { get; set; } = true;

	public List<string> ProtectedAssetPatterns { get; set; } = new List<string> { "game:textures/block/stone", "game:textures/block/ore", "game:textures/block/rock", "game:shaders/" };

	public int ProtectedAssetDetailedEntries { get; set; } = 96;

	public List<string> ApprovedClientBuildHashes { get; set; } = new List<string>();

	public List<string> RequiredClientModIds { get; set; } = new List<string> { "newfiesanticheatclient" };

	public List<string> ForbiddenModIds { get; set; } = DefaultForbiddenModIds.ToList();

	public int FingerprintReleaseDepth { get; set; } = 6;

	public List<AntiCheatBlacklistProfile> BlacklistProfiles { get; set; } = new List<AntiCheatBlacklistProfile>();

	public string ActiveBlacklistProfileId { get; set; } = "";

	public List<AntiCheatForbiddenFingerprint> ForbiddenFingerprints { get; set; } = new List<AntiCheatForbiddenFingerprint>();

	public List<string> AllowedExtraCodeModIds { get; set; } = new List<string>();

	public List<AntiCheatConsequenceRule> ConsequenceRules { get; set; } = new List<AntiCheatConsequenceRule>();

	public void SeedDefaultFingerprintsIfEmpty()
	{
		if (ForbiddenFingerprints == null)
		{
			List<AntiCheatForbiddenFingerprint> list = (ForbiddenFingerprints = new List<AntiCheatForbiddenFingerprint>());
		}
		AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint = ForbiddenFingerprints.FirstOrDefault((AntiCheatForbiddenFingerprint x) => x != null && string.Equals(x.ModId, "modmenu", StringComparison.OrdinalIgnoreCase) && (string.Equals(x.PackageSha256, "9f97c05e33cccab034b7ea5e8a6a2a0ab80cc6ebd374516a86287628eb048986", StringComparison.OrdinalIgnoreCase) || (x.AssemblySha256s ?? new List<string>()).Any((string h) => string.Equals(h, "d8e0e89c93aaebf171ee8aef67841273c10b98b941f48d2476a7125da99541a2", StringComparison.OrdinalIgnoreCase))));
		if (antiCheatForbiddenFingerprint != null)
		{
			if (string.IsNullOrWhiteSpace(antiCheatForbiddenFingerprint.ContentSha256))
			{
				antiCheatForbiddenFingerprint.ContentSha256 = "be29fd5a9fca7b22368dd0e8ad16e9fb50bd7efaf7cc7ce63f19ae867502e456";
			}
			AntiCheatForbiddenFingerprint antiCheatForbiddenFingerprint2 = antiCheatForbiddenFingerprint;
			if (antiCheatForbiddenFingerprint2.AssemblyNames == null)
			{
				List<string> list3 = (antiCheatForbiddenFingerprint2.AssemblyNames = new List<string>());
			}
			if (!antiCheatForbiddenFingerprint.AssemblyNames.Contains("ModMenu", StringComparer.OrdinalIgnoreCase))
			{
				antiCheatForbiddenFingerprint.AssemblyNames.Add("ModMenu");
			}
			antiCheatForbiddenFingerprint2 = antiCheatForbiddenFingerprint;
			if (antiCheatForbiddenFingerprint2.AssemblySha256s == null)
			{
				List<string> list3 = (antiCheatForbiddenFingerprint2.AssemblySha256s = new List<string>());
			}
			if (!antiCheatForbiddenFingerprint.AssemblySha256s.Contains("d8e0e89c93aaebf171ee8aef67841273c10b98b941f48d2476a7125da99541a2", StringComparer.OrdinalIgnoreCase))
			{
				antiCheatForbiddenFingerprint.AssemblySha256s.Add("d8e0e89c93aaebf171ee8aef67841273c10b98b941f48d2476a7125da99541a2");
			}
			antiCheatForbiddenFingerprint2 = antiCheatForbiddenFingerprint;
			if (antiCheatForbiddenFingerprint2.ModSystemTypeNames == null)
			{
				List<string> list3 = (antiCheatForbiddenFingerprint2.ModSystemTypeNames = new List<string>());
			}
			{
				foreach (string modSystemTypeName in CreateModMenu121Fingerprint().ModSystemTypeNames)
				{
					if (!antiCheatForbiddenFingerprint.ModSystemTypeNames.Contains(modSystemTypeName, StringComparer.Ordinal))
					{
						antiCheatForbiddenFingerprint.ModSystemTypeNames.Add(modSystemTypeName);
					}
				}
				return;
			}
		}
		ForbiddenFingerprints.Add(CreateModMenu121Fingerprint());
	}

	private static AntiCheatForbiddenFingerprint CreateModMenu121Fingerprint()
	{
		return new AntiCheatForbiddenFingerprint
		{
			ModId = "modmenu",
			Source = "built-in",
			ResolvedVersion = "1.2.1",
			ResolvedFileName = "ModMenu_1.2.1.zip",
			PackageSha256 = "9f97c05e33cccab034b7ea5e8a6a2a0ab80cc6ebd374516a86287628eb048986",
			ContentSha256 = "be29fd5a9fca7b22368dd0e8ad16e9fb50bd7efaf7cc7ce63f19ae867502e456",
			AssemblyNames = new List<string> { "ModMenu" },
			AssemblySha256s = new List<string> { "d8e0e89c93aaebf171ee8aef67841273c10b98b941f48d2476a7125da99541a2" },
			ModSystemTypeNames = new List<string> { "ModMenu.ModMenuSystem", "ModMenu.Patches.MoveSpeedPatch", "ModMenu.Patches.ReachMouseRayPatch", "ModMenu.Patches.InstantMinePatch", "ModMenu.Patches.VeinMinePatch" }
		};
	}

	public void SeedDefaultConsequenceRulesIfEmpty()
	{
		if (ConsequenceRules == null)
		{
			List<AntiCheatConsequenceRule> list = (ConsequenceRules = new List<AntiCheatConsequenceRule>());
		}
		if (ConsequenceRules.Count == 0)
		{
			ConsequenceRules.AddRange(CreateDefaultConsequenceRules());
		}
	}

	private static IEnumerable<AntiCheatConsequenceRule> CreateDefaultConsequenceRules()
	{
		yield return new AntiCheatConsequenceRule
		{
			Name = "Log forbidden client mod",
			TriggerType = "FindingCode",
			TriggerValue = "FORBIDDEN_MOD",
			Action = "Log",
			Duration = "30m",
			Message = "Anti-cheat detected forbidden mod {subject} on {player}."
		};
		yield return new AntiCheatConsequenceRule
		{
			Name = "Log suspicious status",
			TriggerType = "Status",
			TriggerValue = "Suspicious",
			Action = "Log",
			Duration = "30m",
			Message = "Anti-cheat marked {player} suspicious (risk {risk})."
		};
	}

	public void Clamp()
	{
		int schemaVersion = SchemaVersion;
		SchemaVersion = 62;
		PacketGuardConfirmedSpeedRepeatRisk = Math.Clamp(PacketGuardConfirmedSpeedRepeatRisk, 0, 50);
		OreShieldDecoysEnabled = false;
		OreShieldDecoyPercent = 0;
		OreShieldDecoyClusterSize = Math.Clamp(OreShieldDecoyClusterSize, 1, 4);
		OreShieldRevealOnUnknownNeighbor = false;
		OreShieldRevealRadiusBlocks = 0;
		if (schemaVersion < 59)
		{
			OreShieldRevealWholeVeinOnMine = false;
		}
		OreShieldMaxVeinRevealBlocks = Math.Clamp(OreShieldMaxVeinRevealBlocks, 64, 8192);
		OreShieldDiscoveryRangeBlocks = Math.Clamp(OreShieldDiscoveryRangeBlocks, 16, 192);
		OreShieldVisibilityScanIntervalMs = Math.Clamp(OreShieldVisibilityScanIntervalMs, 100, 2000);
		OreShieldMaxLosChecksPerPass = Math.Clamp(OreShieldMaxLosChecksPerPass, 16, 512);
		OreShieldViewConeDegrees = Math.Clamp(OreShieldViewConeDegrees, 90, 180);
		OreShieldIdleRescanMs = Math.Clamp(OreShieldIdleRescanMs, 500, 15000);
		OreShieldChunkCacheMiB = Math.Clamp(OreShieldChunkCacheMiB, 8, 1024);
		OreShieldMaxCachedChunks = Math.Clamp(OreShieldMaxCachedChunks, 256, 16384);
		if (OreShieldAdditionalOrePatterns == null)
		{
			List<string> list = (OreShieldAdditionalOrePatterns = new List<string>());
		}
		if (OreShieldExcludedBlockPatterns == null)
		{
			List<string> list = (OreShieldExcludedBlockPatterns = new List<string>());
		}
		OreShieldAdditionalOrePatterns = (from x in OreShieldAdditionalOrePatterns
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
		OreShieldExcludedBlockPatterns = (from x in OreShieldExcludedBlockPatterns
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToList();
		ChallengeTimeoutSeconds = Math.Clamp(ChallengeTimeoutSeconds, 10, 120);
		SelfAttestationMethodProbeCount = Math.Clamp(SelfAttestationMethodProbeCount, 2, 12);
		SelfAttestationTypeProbeCount = Math.Clamp(SelfAttestationTypeProbeCount, 1, 8);
		SelfAttestationFailuresBeforeKick = Math.Clamp(SelfAttestationFailuresBeforeKick, 2, 4);
		ClientBuildManifestPublicKeyBase64 = (ClientBuildManifestPublicKeyBase64 ?? "").Trim();
		if (schemaVersion < 17)
		{
			ClientHelloGraceSeconds = Math.Max(ClientHelloGraceSeconds, 60);
		}
		if (schemaVersion < 50)
		{
			StartupIntegrityGateBlockWorldInteractions = false;
		}
		if (schemaVersion < 47)
		{
			if (ClientHelloGraceSeconds == 60)
			{
				ClientHelloGraceSeconds = 90;
			}
			if (ClientHelloDisconnectDelaySeconds == 30)
			{
				ClientHelloDisconnectDelaySeconds = 60;
			}
			if (HeartbeatTimeoutSeconds == 90)
			{
				HeartbeatTimeoutSeconds = 180;
			}
		}
		if (schemaVersion < 18)
		{
			PacketGuardMiningCadenceValidationEnabled = false;
		}
		if (schemaVersion < 21 && Math.Abs(PacketGuardHardMoveDistance - 32.0) < 0.001)
		{
			PacketGuardHardMoveDistance = 8.0;
		}
		if (schemaVersion < 22)
		{
			if (PacketGuardMountProfileConfirmSamples == 2)
			{
				PacketGuardMountProfileConfirmSamples = 3;
			}
			if (PacketGuardMountProfileConfirmWindowMs == 700)
			{
				PacketGuardMountProfileConfirmWindowMs = 1200;
			}
			if (Math.Abs(PacketGuardShipwrightMaxHorizontalSpeed - 120.0) < 0.001)
			{
				PacketGuardShipwrightMaxHorizontalSpeed = 32.0;
			}
			if (Math.Abs(PacketGuardShipwrightMaxVerticalSpeed - 30.0) < 0.001)
			{
				PacketGuardShipwrightMaxVerticalSpeed = 20.0;
			}
			if (Math.Abs(PacketGuardShipwrightMaxAcceleration - 700.0) < 0.001)
			{
				PacketGuardShipwrightMaxAcceleration = 300.0;
			}
			if (Math.Abs(PacketGuardDraconisMaxHorizontalSpeed - 80.0) < 0.001)
			{
				PacketGuardDraconisMaxHorizontalSpeed = 26.0;
			}
			if (Math.Abs(PacketGuardDraconisMaxVerticalSpeed - 65.0) < 0.001)
			{
				PacketGuardDraconisMaxVerticalSpeed = 40.0;
			}
			if (Math.Abs(PacketGuardDraconisMaxAcceleration - 500.0) < 0.001)
			{
				PacketGuardDraconisMaxAcceleration = 260.0;
			}
			if (Math.Abs(PacketGuardUnknownMountMaxHorizontalSpeed - 100.0) < 0.001)
			{
				PacketGuardUnknownMountMaxHorizontalSpeed = 40.0;
			}
			if (Math.Abs(PacketGuardUnknownMountMaxVerticalSpeed - 60.0) < 0.001)
			{
				PacketGuardUnknownMountMaxVerticalSpeed = 40.0;
			}
			if (Math.Abs(PacketGuardUnknownMountMaxAcceleration - 600.0) < 0.001)
			{
				PacketGuardUnknownMountMaxAcceleration = 350.0;
			}
		}
		if (schemaVersion < 24)
		{
			if (Math.Abs(PacketGuardDraconisMaxHorizontalSpeed - 26.0) < 0.001)
			{
				PacketGuardDraconisMaxHorizontalSpeed = 65.0;
			}
			if (Math.Abs(PacketGuardDraconisMaxVerticalSpeed - 40.0) < 0.001)
			{
				PacketGuardDraconisMaxVerticalSpeed = 50.0;
			}
			if (Math.Abs(PacketGuardDraconisMaxAcceleration - 260.0) < 0.001)
			{
				PacketGuardDraconisMaxAcceleration = 420.0;
			}
		}
		if (schemaVersion < 25)
		{
			if (Math.Abs(PacketGuardShipwrightMaxHorizontalSpeed - 32.0) < 0.001)
			{
				PacketGuardShipwrightMaxHorizontalSpeed = 55.0;
			}
			if (Math.Abs(PacketGuardDraconisMaxHorizontalSpeed - 65.0) < 0.001)
			{
				PacketGuardDraconisMaxHorizontalSpeed = 80.0;
			}
		}
		if (schemaVersion < 26)
		{
			PacketGuardMovementRiskMount = ((PacketGuardMovementRiskMount <= 0.0) ? 35.0 : PacketGuardMovementRiskMount);
		}
		if (schemaVersion < 27)
		{
			PacketGuardRollbackValidateCollision = true;
			PacketGuardMountRiderCorrelationEnabled = true;
		}
		if (schemaVersion < 28)
		{
			PacketGuardPlayerWarningEnabled = false;
			if (PacketGuardPlayerWarningCooldownSeconds <= 0)
			{
				PacketGuardPlayerWarningCooldownSeconds = 15;
			}
		}
		StartupChallengeTimeoutSeconds = Math.Clamp(StartupChallengeTimeoutSeconds, 60, 900);
		StartupChallengeHeartbeatExtensionSeconds = Math.Clamp(StartupChallengeHeartbeatExtensionSeconds, 30, 300);
		StartupChallengeHardLimitSeconds = Math.Clamp(StartupChallengeHardLimitSeconds, StartupChallengeTimeoutSeconds, 1800);
		ClientHelloGraceSeconds = Math.Clamp(ClientHelloGraceSeconds, 15, 300);
		ClientHelloDisconnectDelaySeconds = Math.Clamp(ClientHelloDisconnectDelaySeconds, 10, 300);
		HeartbeatTimeoutSeconds = Math.Clamp(HeartbeatTimeoutSeconds, 30, 900);
		RescanMinutes = Math.Clamp(RescanMinutes, 1, 1440);
		RuntimeChangeRescanCooldownSeconds = Math.Clamp(RuntimeChangeRescanCooldownSeconds, 10, 300);
		SuspectPackageCollectionMaxMb = Math.Clamp(SuspectPackageCollectionMaxMb, 8, 256);
		SuspectPackageCollectionTimeoutSeconds = Math.Clamp(SuspectPackageCollectionTimeoutSeconds, 30, 300);
		SuspectPackageDeclineAction = NormalizeChoice(SuspectPackageDeclineAction, "None", "Disconnect", "TempBan");
		SuspectPackageDeclineTempBanHours = Math.Clamp(SuspectPackageDeclineTempBanHours, 1, 720);
		SuspectPackageUploadKbps = Math.Clamp(SuspectPackageUploadKbps, 128, 4096);
		SimilarityReviewThresholdPercent = Math.Clamp(SimilarityReviewThresholdPercent, 50, 95);
		SimilarityHighThresholdPercent = Math.Clamp(SimilarityHighThresholdPercent, SimilarityReviewThresholdPercent + 1, 100);
		PacketGuardPlayerWarningCooldownSeconds = Math.Clamp(PacketGuardPlayerWarningCooldownSeconds, 3, 300);
		PacketGuardMaxViewDistanceBlocks = Math.Clamp(PacketGuardMaxViewDistanceBlocks, 64, 4096);
		PacketGuardIngressMaxPacketKiB = Math.Clamp(PacketGuardIngressMaxPacketKiB, 128, 8192);
		PacketGuardIngressPacketsPerSecond = Math.Clamp(PacketGuardIngressPacketsPerSecond, 60, 2000);
		PacketGuardIngressPacketBurst = Math.Clamp(PacketGuardIngressPacketBurst, PacketGuardIngressPacketsPerSecond, 6000);
		PacketGuardBlockPacketsPerSecond = Math.Clamp(PacketGuardBlockPacketsPerSecond, 10, 500);
		PacketGuardBlockPacketBurst = Math.Clamp(PacketGuardBlockPacketBurst, PacketGuardBlockPacketsPerSecond, 1500);
		PacketGuardSettingsPacketsPerSecond = Math.Clamp(PacketGuardSettingsPacketsPerSecond, 1, 20);
		PacketGuardSettingsPacketBurst = Math.Clamp(PacketGuardSettingsPacketBurst, PacketGuardSettingsPacketsPerSecond, 60);
		PacketGuardIngressFindingCooldownSeconds = Math.Clamp(PacketGuardIngressFindingCooldownSeconds, 2, 120);
		if (schemaVersion < 61)
		{
			PacketGuardUnverifiedTeleportReconciliationEnabled = false;
		}
		PacketGuardMovementSampleMs = Math.Clamp(PacketGuardMovementSampleMs, 75, 500);
		PacketGuardFlightReviewConfirmSeconds = Math.Clamp(PacketGuardFlightReviewConfirmSeconds, 3, 60);
		PacketGuardFlightReviewSampleMs = Math.Clamp(PacketGuardFlightReviewSampleMs, 200, 1000);
		PacketGuardHardMoveDistance = Math.Clamp(PacketGuardHardMoveDistance, 4.0, 96.0);
		PacketGuardMovementMaxSampleScale = Math.Clamp(PacketGuardMovementMaxSampleScale, 1.0, 8.0);
		PacketGuardMovementConfirmSamples = Math.Clamp(PacketGuardMovementConfirmSamples, 1, 4);
		if (schemaVersion < 34)
		{
			if (PacketGuardAccelerationConfirmWindowMs == 650)
			{
				PacketGuardAccelerationConfirmWindowMs = 900;
			}
			if (PacketGuardNoclipRequiredCollisionSamples == 2)
			{
				PacketGuardNoclipRequiredCollisionSamples = 3;
			}
			if (Math.Abs(PacketGuardNoclipMinPenetrationDepth - 0.14) < 0.001)
			{
				PacketGuardNoclipMinPenetrationDepth = 0.18;
			}
			if (PacketGuardNoclipEndpointConfirmSamples == 2)
			{
				PacketGuardNoclipEndpointConfirmSamples = 3;
			}
		}
		if (schemaVersion < 36)
		{
			PacketGuardNoclipEnabled = false;
		}
		PacketGuardMovementConfirmWindowMs = Math.Clamp(PacketGuardMovementConfirmWindowMs, 100, 2000);
		PacketGuardMovementExtremeMultiplier = Math.Clamp(PacketGuardMovementExtremeMultiplier, 1.5, 6.0);
		PacketGuardCorrectionGraceMs = Math.Clamp(PacketGuardCorrectionGraceMs, 250, 5000);
		PacketGuardMaxHorizontalSpeed = Math.Clamp(PacketGuardMaxHorizontalSpeed, 8.0, 200.0);
		PacketGuardGroundMaxHorizontalSpeed = Math.Clamp(PacketGuardGroundMaxHorizontalSpeed, 6.0, 200.0);
		PacketGuardSwimmingMaxHorizontalSpeed = Math.Clamp(PacketGuardSwimmingMaxHorizontalSpeed, 4.0, 120.0);
		PacketGuardAirborneMaxHorizontalSpeed = Math.Clamp(PacketGuardAirborneMaxHorizontalSpeed, 6.0, 200.0);
		PacketGuardSpeedWindowMs = Math.Clamp(PacketGuardSpeedWindowMs, 250, 3000);
		PacketGuardSpeedConfirmWindows = Math.Clamp(PacketGuardSpeedConfirmWindows, 1, 5);
		PacketGuardAccelerationMinSpeed = Math.Clamp(PacketGuardAccelerationMinSpeed, 4.0, 100.0);
		PacketGuardMaxHorizontalAcceleration = Math.Clamp(PacketGuardMaxHorizontalAcceleration, 20.0, 1000.0);
		PacketGuardAccelerationConfirmSamples = Math.Clamp(PacketGuardAccelerationConfirmSamples, 1, 6);
		PacketGuardAccelerationConfirmWindowMs = Math.Clamp(PacketGuardAccelerationConfirmWindowMs, 100, 3000);
		PacketGuardAccelerationSampleMinMs = Math.Clamp(PacketGuardAccelerationSampleMinMs, 100, 1000);
		PacketGuardAccelerationSmoothingFactor = Math.Clamp(PacketGuardAccelerationSmoothingFactor, 0.05, 1.0);
		PacketGuardNoclipMinPathDistance = Math.Clamp(PacketGuardNoclipMinPathDistance, 0.5, 8.0);
		PacketGuardNoclipSampleStep = Math.Clamp(PacketGuardNoclipSampleStep, 0.1, 0.75);
		PacketGuardNoclipRequiredCollisionSamples = Math.Clamp(PacketGuardNoclipRequiredCollisionSamples, 1, 6);
		PacketGuardNoclipMinPenetrationDepth = Math.Clamp(PacketGuardNoclipMinPenetrationDepth, 0.03, 0.75);
		PacketGuardNoclipEndpointMinPenetrationDepth = Math.Clamp(PacketGuardNoclipEndpointMinPenetrationDepth, 0.05, 0.75);
		PacketGuardNoclipEndpointConfirmSamples = Math.Clamp(PacketGuardNoclipEndpointConfirmSamples, 1, 6);
		PacketGuardNoclipEndpointMinNetMovement = Math.Clamp(PacketGuardNoclipEndpointMinNetMovement, 0.1, 2.0);
		PacketGuardSafePositionHistorySize = Math.Clamp(PacketGuardSafePositionHistorySize, 4, 64);
		PacketGuardSafePositionMaxAgeMs = Math.Clamp(PacketGuardSafePositionMaxAgeMs, 1000, 30000);
		PacketGuardRollbackSafetyLagMs = Math.Clamp(PacketGuardRollbackSafetyLagMs, 0, 3000);
		PacketGuardRollbackValidationCandidates = Math.Clamp(PacketGuardRollbackValidationCandidates, 1, 32);
		PacketGuardRollbackAntiLoopWindowMs = Math.Clamp(PacketGuardRollbackAntiLoopWindowMs, 250, 10000);
		PacketGuardRollbackAntiLoopRisk = Math.Clamp(PacketGuardRollbackAntiLoopRisk, 1.0, 100.0);
		PacketGuardMovementDuplicateFindingCooldownMs = Math.Clamp(PacketGuardMovementDuplicateFindingCooldownMs, 0, 10000);
		PacketGuardUnverifiedTeleportMinDistance = Math.Clamp(PacketGuardUnverifiedTeleportMinDistance, 32.0, 4096.0);
		PacketGuardUnverifiedTeleportRepeatWindowMs = Math.Clamp(PacketGuardUnverifiedTeleportRepeatWindowMs, 5000, 300000);
		PacketGuardUnverifiedTeleportRepeatCount = Math.Clamp(PacketGuardUnverifiedTeleportRepeatCount, 2, 10);
		PacketGuardMovementRiskDecayPerSecond = Math.Clamp(PacketGuardMovementRiskDecayPerSecond, 0.1, 25.0);
		PacketGuardMovementRiskAcceleration = Math.Clamp(PacketGuardMovementRiskAcceleration, 1.0, 100.0);
		PacketGuardMovementRiskSpeed = Math.Clamp(PacketGuardMovementRiskSpeed, 1.0, 100.0);
		PacketGuardMovementRiskNoclip = Math.Clamp(PacketGuardMovementRiskNoclip, 1.0, 100.0);
		PacketGuardMovementRiskHardMove = Math.Clamp(PacketGuardMovementRiskHardMove, 1.0, 100.0);
		PacketGuardMovementRiskMount = Math.Clamp(PacketGuardMovementRiskMount, 1.0, 100.0);
		PacketGuardMovementConfidenceCorrelationWindowMs = Math.Clamp(PacketGuardMovementConfidenceCorrelationWindowMs, 250, 10000);
		PacketGuardMovementConfidenceCorrelationBonus = Math.Clamp(PacketGuardMovementConfidenceCorrelationBonus, 0.0, 50.0);
		PacketGuardMovementConfidenceGuardedThreshold = Math.Clamp(PacketGuardMovementConfidenceGuardedThreshold, 1.0, 95.0);
		PacketGuardMovementConfidenceHighThreshold = Math.Clamp(PacketGuardMovementConfidenceHighThreshold, PacketGuardMovementConfidenceGuardedThreshold + 1.0, 98.0);
		PacketGuardMovementConfidenceCriticalThreshold = Math.Clamp(PacketGuardMovementConfidenceCriticalThreshold, PacketGuardMovementConfidenceHighThreshold + 1.0, 100.0);
		PacketGuardMountGraceMs = Math.Clamp(PacketGuardMountGraceMs, 250, 10000);
		PacketGuardMountProfileConfirmSamples = Math.Clamp(PacketGuardMountProfileConfirmSamples, 1, 5);
		PacketGuardMountProfileConfirmWindowMs = Math.Clamp(PacketGuardMountProfileConfirmWindowMs, 150, 3000);
		PacketGuardShipwrightMaxHorizontalSpeed = Math.Clamp(PacketGuardShipwrightMaxHorizontalSpeed, 20.0, 300.0);
		PacketGuardShipwrightMaxVerticalSpeed = Math.Clamp(PacketGuardShipwrightMaxVerticalSpeed, 5.0, 150.0);
		PacketGuardShipwrightMaxAcceleration = Math.Clamp(PacketGuardShipwrightMaxAcceleration, 50.0, 3000.0);
		PacketGuardShipwrightMaxRiderDistance = Math.Clamp(PacketGuardShipwrightMaxRiderDistance, 4.0, 64.0);
		PacketGuardDraconisMaxHorizontalSpeed = Math.Clamp(PacketGuardDraconisMaxHorizontalSpeed, 20.0, 300.0);
		PacketGuardDraconisMaxVerticalSpeed = Math.Clamp(PacketGuardDraconisMaxVerticalSpeed, 5.0, 150.0);
		PacketGuardDraconisMaxAcceleration = Math.Clamp(PacketGuardDraconisMaxAcceleration, 50.0, 3000.0);
		PacketGuardDraconisMaxRiderDistance = Math.Clamp(PacketGuardDraconisMaxRiderDistance, 4.0, 64.0);
		PacketGuardUnknownMountMaxHorizontalSpeed = Math.Clamp(PacketGuardUnknownMountMaxHorizontalSpeed, 20.0, 300.0);
		PacketGuardUnknownMountMaxVerticalSpeed = Math.Clamp(PacketGuardUnknownMountMaxVerticalSpeed, 5.0, 150.0);
		PacketGuardUnknownMountMaxAcceleration = Math.Clamp(PacketGuardUnknownMountMaxAcceleration, 50.0, 3000.0);
		PacketGuardMountAccelerationMinSpeedRatio = Math.Clamp(PacketGuardMountAccelerationMinSpeedRatio, 0.5, 1.25);
		PacketGuardDiagnosticSpeedHistorySamples = Math.Clamp(PacketGuardDiagnosticSpeedHistorySamples, 3, 16);
		PacketGuardUnknownMountMaxRiderDistance = Math.Clamp(PacketGuardUnknownMountMaxRiderDistance, 4.0, 64.0);
		PacketGuardShipwrightMaxRiderRelativeSpeed = Math.Clamp(PacketGuardShipwrightMaxRiderRelativeSpeed, 5.0, 150.0);
		PacketGuardDraconisMaxRiderRelativeSpeed = Math.Clamp(PacketGuardDraconisMaxRiderRelativeSpeed, 5.0, 150.0);
		PacketGuardUnknownMountMaxRiderRelativeSpeed = Math.Clamp(PacketGuardUnknownMountMaxRiderRelativeSpeed, 5.0, 150.0);
		PacketGuardEngineTeleportGraceMs = Math.Clamp(PacketGuardEngineTeleportGraceMs, 500, 15000);
		PacketGuardTPRunesGraceMs = Math.Clamp(PacketGuardTPRunesGraceMs, 1000, 20000);
		PacketGuardTh3EssentialsSpawnGraceMs = Math.Clamp(PacketGuardTh3EssentialsSpawnGraceMs, 1000, 20000);
		PacketGuardReachTolerance = Math.Clamp(PacketGuardReachTolerance, 0.25, 3.0);
		PacketGuardReachMaxLatencyCompensation = Math.Clamp(PacketGuardReachMaxLatencyCompensation, 0.0, 3.0);
		PacketGuardReachPositionHistoryMs = Math.Clamp(PacketGuardReachPositionHistoryMs, 100, 1500);
		PacketGuardReachMaxRewindMs = Math.Clamp(PacketGuardReachMaxRewindMs, 100, PacketGuardReachPositionHistoryMs);
		PacketGuardReachUseExtraTolerance = Math.Clamp(PacketGuardReachUseExtraTolerance, 0.0, 1.5);
		PacketGuardReachBreakExtraTolerance = Math.Clamp(PacketGuardReachBreakExtraTolerance, 0.0, 1.5);
		PacketGuardReachStrikeDecayMs = Math.Clamp(PacketGuardReachStrikeDecayMs, 500, 10000);
		PacketGuardReachNearThresholdStrikes = Math.Clamp(PacketGuardReachNearThresholdStrikes, 1, 8);
		PacketGuardReachStrikeWindowMs = Math.Clamp(PacketGuardReachStrikeWindowMs, 500, 10000);
		PacketGuardReachHardExcess = Math.Clamp(PacketGuardReachHardExcess, 0.5, 4.0);
		PacketGuardReachDistinctTargetsRequired = Math.Clamp(PacketGuardReachDistinctTargetsRequired, 1, 6);
		PacketGuardVeinWindowMs = Math.Clamp(PacketGuardVeinWindowMs, 500, 5000);
		PacketGuardConnectedBreakLimit = Math.Clamp(PacketGuardConnectedBreakLimit, 8, 40);
		PacketGuardConnectedBreakConfirmations = Math.Clamp(PacketGuardConnectedBreakConfirmations, 1, 5);
		PacketGuardConnectedBreakExtremeMultiplier = Math.Clamp(PacketGuardConnectedBreakExtremeMultiplier, 1.1, 4.0);
		PacketGuardMiningDuplicateTargetMs = Math.Clamp(PacketGuardMiningDuplicateTargetMs, 100, 1500);
		PacketGuardMiningCadenceMinExpectedMs = Math.Clamp(PacketGuardMiningCadenceMinExpectedMs, 100.0, 10000.0);
		PacketGuardMiningCadenceSuspiciousRatio = Math.Clamp(PacketGuardMiningCadenceSuspiciousRatio, 0.1, 0.85);
		PacketGuardMiningCadenceConfirmations = Math.Clamp(PacketGuardMiningCadenceConfirmations, 2, 8);
		PacketGuardMiningCadenceConfirmationWindowMs = Math.Clamp(PacketGuardMiningCadenceConfirmationWindowMs, 1000, 30000);
		PacketGuardTpsDegradedPercent = Math.Clamp(PacketGuardTpsDegradedPercent, 55.0, 99.0);
		PacketGuardTpsCriticalPercent = Math.Clamp(PacketGuardTpsCriticalPercent, 25.0, PacketGuardTpsDegradedPercent - 1.0);
		PacketGuardTpsMaxTimingMultiplier = Math.Clamp(PacketGuardTpsMaxTimingMultiplier, 1.0, 4.0);
		MovementRiskDecaySeconds = Math.Clamp(MovementRiskDecaySeconds, 30, 3600);
		MovementRiskReviewThreshold = Math.Clamp(MovementRiskReviewThreshold, 1, 100);
		MovementRiskSuspiciousThreshold = Math.Clamp(MovementRiskSuspiciousThreshold, MovementRiskReviewThreshold, 100);
		ReachRiskDecaySeconds = Math.Clamp(ReachRiskDecaySeconds, 30, 7200);
		ReachRiskReviewThreshold = Math.Clamp(ReachRiskReviewThreshold, 1, 100);
		ReachRiskSuspiciousThreshold = Math.Clamp(ReachRiskSuspiciousThreshold, ReachRiskReviewThreshold, 100);
		MiningRiskDecaySeconds = Math.Clamp(MiningRiskDecaySeconds, 30, 7200);
		MiningRiskReviewThreshold = Math.Clamp(MiningRiskReviewThreshold, 1, 100);
		MiningRiskSuspiciousThreshold = Math.Clamp(MiningRiskSuspiciousThreshold, MiningRiskReviewThreshold, 100);
		PacketAbuseRiskDecaySeconds = Math.Clamp(PacketAbuseRiskDecaySeconds, 30, 14400);
		PacketAbuseRiskReviewThreshold = Math.Clamp(PacketAbuseRiskReviewThreshold, 1, 100);
		PacketAbuseRiskSuspiciousThreshold = Math.Clamp(PacketAbuseRiskSuspiciousThreshold, PacketAbuseRiskReviewThreshold, 100);
		ClientIntegrityRiskDecaySeconds = Math.Clamp(ClientIntegrityRiskDecaySeconds, 60, 86400);
		ClientIntegrityRiskReviewThreshold = Math.Clamp(ClientIntegrityRiskReviewThreshold, 1, 100);
		ClientIntegrityRiskSuspiciousThreshold = Math.Clamp(ClientIntegrityRiskSuspiciousThreshold, ClientIntegrityRiskReviewThreshold, 100);
		IncidentCorrelationWindowSeconds = Math.Clamp(IncidentCorrelationWindowSeconds, 10, 600);
		IncidentCorrelationRequiredCategories = Math.Clamp(IncidentCorrelationRequiredCategories, 2, 5);
		IncidentCorrelationMaxRiskBonus = Math.Clamp(IncidentCorrelationMaxRiskBonus, 0, 35);
		if (schemaVersion < 41 && HistoryMaxRecords == 10000)
		{
			HistoryMaxRecords = 500;
		}
		HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 1, 3650);
		HistoryMaxRecords = Math.Clamp(HistoryMaxRecords, 100, 1000);
		if (RequiredClientModIds == null)
		{
			List<string> list = (RequiredClientModIds = new List<string>());
		}
		if (ForbiddenModIds == null)
		{
			List<string> list = (ForbiddenModIds = new List<string>());
		}
		if (ForbiddenFingerprints == null)
		{
			List<AntiCheatForbiddenFingerprint> list6 = (ForbiddenFingerprints = new List<AntiCheatForbiddenFingerprint>());
		}
		if (BlacklistProfiles == null)
		{
			List<AntiCheatBlacklistProfile> list8 = (BlacklistProfiles = new List<AntiCheatBlacklistProfile>());
		}
		if (ApprovedClientBuildHashes == null)
		{
			List<string> list = (ApprovedClientBuildHashes = new List<string>());
		}
		if (AllowedExtraCodeModIds == null)
		{
			List<string> list = (AllowedExtraCodeModIds = new List<string>());
		}
		if (ConsequenceRules == null)
		{
			List<AntiCheatConsequenceRule> list12 = (ConsequenceRules = new List<AntiCheatConsequenceRule>());
		}
		if (SuspiciousRuntimeTerms == null)
		{
			List<string> list = (SuspiciousRuntimeTerms = new List<string>());
		}
		if (ProtectedAssetPatterns == null)
		{
			List<string> list = (ProtectedAssetPatterns = new List<string>());
		}
		if (StartupCoreAssemblyNames == null)
		{
			List<string> list = (StartupCoreAssemblyNames = new List<string>());
		}
		if (schemaVersion < 2 && ConsequenceRules.Count == 0)
		{
			SeedDefaultConsequenceRulesIfEmpty();
		}
		if (schemaVersion < 4)
		{
			ForbiddenModIds.AddRange(DefaultForbiddenModIds);
		}
		if (schemaVersion < 12)
		{
			SeedDefaultFingerprintsIfEmpty();
		}
		SchemaVersion = Math.Max(SchemaVersion, 62);
		RequiredClientModIds = NormalizeIds(RequiredClientModIds);
		FingerprintReleaseDepth = Math.Clamp((FingerprintReleaseDepth <= 0) ? 6 : FingerprintReleaseDepth, 1, 20);
		ForbiddenModIds = NormalizeIds(ForbiddenModIds);
		AntiCheatBlacklistProfile antiCheatBlacklistProfile = AntiCheatDefaultBlacklistProfileData.CreateProfile();
		AntiCheatBlacklistProfile antiCheatBlacklistProfile2 = BlacklistProfiles.FirstOrDefault((AntiCheatBlacklistProfile x) => string.Equals(x.Id, "newfies-default-unfair-mods", StringComparison.OrdinalIgnoreCase));
		if (antiCheatBlacklistProfile2 == null)
		{
			BlacklistProfiles.Add(antiCheatBlacklistProfile);
		}
		else if (schemaVersion < 33)
		{
			antiCheatBlacklistProfile2.Name = antiCheatBlacklistProfile.Name;
			antiCheatBlacklistProfile2.ModIds = antiCheatBlacklistProfile.ModIds;
			antiCheatBlacklistProfile2.Fingerprints = antiCheatBlacklistProfile.Fingerprints;
			if (antiCheatBlacklistProfile2.CreatedUtc <= 0)
			{
				antiCheatBlacklistProfile2.CreatedUtc = antiCheatBlacklistProfile.CreatedUtc;
			}
			antiCheatBlacklistProfile2.UpdatedUtc = antiCheatBlacklistProfile.UpdatedUtc;
		}
		if (schemaVersion < 33)
		{
			foreach (AntiCheatBlacklistProfile item in BlacklistProfiles.Where((AntiCheatBlacklistProfile x) => x != null && !string.Equals(x.Id, "newfies-default-unfair-mods", StringComparison.OrdinalIgnoreCase)))
			{
				AntiCheatBlacklistProfile antiCheatBlacklistProfile3 = item;
				if (antiCheatBlacklistProfile3.Fingerprints == null)
				{
					List<AntiCheatForbiddenFingerprint> list6 = (antiCheatBlacklistProfile3.Fingerprints = new List<AntiCheatForbiddenFingerprint>());
				}
				if (item.Fingerprints.Count == 0)
				{
					HashSet<string> ids = new HashSet<string>(item.ModIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
					item.Fingerprints = AntiCheatDefaultBlacklistProfileData.CloneFingerprints(ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint fp) => fp != null && ids.Contains(fp.ModId ?? "")));
				}
			}
		}
		BlacklistProfiles = BlacklistProfiles.Where((AntiCheatBlacklistProfile x) => x != null).Take(32).ToList();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AntiCheatBlacklistProfile blacklistProfile in BlacklistProfiles)
		{
			string text = (blacklistProfile.Id ?? "").Trim();
			if (text.Length == 0 || !hashSet.Add(text))
			{
				text = Guid.NewGuid().ToString("N");
				hashSet.Add(text);
			}
			blacklistProfile.Id = text;
			blacklistProfile.Name = Clean(blacklistProfile.Name, 64, "Blacklist profile");
			blacklistProfile.ModIds = NormalizeIds(blacklistProfile.ModIds ?? new List<string>()).Take(256).ToList();
			AntiCheatBlacklistProfile antiCheatBlacklistProfile3 = blacklistProfile;
			if (antiCheatBlacklistProfile3.Fingerprints == null)
			{
				List<AntiCheatForbiddenFingerprint> list6 = (antiCheatBlacklistProfile3.Fingerprints = new List<AntiCheatForbiddenFingerprint>());
			}
			blacklistProfile.Fingerprints = blacklistProfile.Fingerprints.Where((AntiCheatForbiddenFingerprint x) => x != null).Take(256).ToList();
			if (blacklistProfile.CreatedUtc < 0)
			{
				blacklistProfile.CreatedUtc = 0L;
			}
			if (blacklistProfile.UpdatedUtc < blacklistProfile.CreatedUtc)
			{
				blacklistProfile.UpdatedUtc = blacklistProfile.CreatedUtc;
			}
		}
		ActiveBlacklistProfileId = (ActiveBlacklistProfileId ?? "").Trim();
		if (ActiveBlacklistProfileId.Length > 0 && !BlacklistProfiles.Any((AntiCheatBlacklistProfile x) => string.Equals(x.Id, ActiveBlacklistProfileId, StringComparison.OrdinalIgnoreCase)))
		{
			ActiveBlacklistProfileId = "";
		}
		AllowedExtraCodeModIds = NormalizeIds(AllowedExtraCodeModIds);
		SuspiciousRuntimeTerms = (from x in SuspiciousRuntimeTerms
			where !string.IsNullOrWhiteSpace(x)
			select Clean(x, 48, "") into x
			where x.Length >= 3
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
		ProtectedAssetPatterns = (from x in ProtectedAssetPatterns
			where !string.IsNullOrWhiteSpace(x)
			select Clean(x, 160, "").ToLowerInvariant() into x
			where x.Length > 0
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
		ProtectedAssetDetailedEntries = Math.Clamp(ProtectedAssetDetailedEntries, 0, 128);
		StartupIntegrityGateNoticeCooldownSeconds = Math.Clamp(StartupIntegrityGateNoticeCooldownSeconds, 2, 30);
		if (StrictClientModDbEnforcementEnabled)
		{
			ClientModDbVerificationEnabled = true;
			RequireClient = true;
		}
		StartupCoreAssemblyNames = (from x in StartupCoreAssemblyNames
			where !string.IsNullOrWhiteSpace(x)
			select Clean(x, 128, "") into x
			where x.Length > 0
			select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
		if (StartupCoreAssemblyNames.Count == 0)
		{
			StartupCoreAssemblyNames.Add("VintagestoryAPI");
		}
		ApprovedClientBuildHashes = (from x in (from x in ApprovedClientBuildHashes.Select(NormalizeHash)
				where x.Length == 64
				select x).Distinct(StringComparer.OrdinalIgnoreCase)
			orderby x
			select x).Take(32).ToList();
		ForbiddenFingerprints = ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprint x) => x != null).Take(256).ToList();
		foreach (AntiCheatForbiddenFingerprint forbiddenFingerprint in ForbiddenFingerprints)
		{
			forbiddenFingerprint.ModId = (forbiddenFingerprint.ModId ?? "").Trim().ToLowerInvariant();
			forbiddenFingerprint.Source = Clean(forbiddenFingerprint.Source, 32, "");
			forbiddenFingerprint.ResolvedVersion = Clean(forbiddenFingerprint.ResolvedVersion, 64, "");
			forbiddenFingerprint.ResolvedFileName = Clean(forbiddenFingerprint.ResolvedFileName, 160, "");
			if (forbiddenFingerprint.ResolvedUtc < 0)
			{
				forbiddenFingerprint.ResolvedUtc = 0L;
			}
			forbiddenFingerprint.PackageSha256 = NormalizeHash(forbiddenFingerprint.PackageSha256);
			forbiddenFingerprint.ContentSha256 = NormalizeHash(forbiddenFingerprint.ContentSha256);
			forbiddenFingerprint.AssemblyNames = (from x in forbiddenFingerprint.AssemblyNames ?? new List<string>()
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
			forbiddenFingerprint.AssemblySha256s = (from x in (forbiddenFingerprint.AssemblySha256s ?? new List<string>()).Select(NormalizeHash)
				where x.Length == 64
				select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
			forbiddenFingerprint.AssemblyMvids = (from x in forbiddenFingerprint.AssemblyMvids ?? new List<string>()
				where Guid.TryParse(x, out var _)
				select Guid.Parse(x).ToString("D")).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
			forbiddenFingerprint.AssemblyStructureSha256s = (from x in (forbiddenFingerprint.AssemblyStructureSha256s ?? new List<string>()).Select(NormalizeHash)
				where x.Length == 64
				select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
			forbiddenFingerprint.AssemblyBehaviorSha256s = (from x in (forbiddenFingerprint.AssemblyBehaviorSha256s ?? new List<string>()).Select(NormalizeHash)
				where x.Length == 64
				select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
			forbiddenFingerprint.ModSystemTypeNames = (from x in forbiddenFingerprint.ModSystemTypeNames ?? new List<string>()
				where !string.IsNullOrWhiteSpace(x)
				select x.Trim()).Distinct(StringComparer.Ordinal).Take(64).ToList();
			forbiddenFingerprint.ManualEntries = (forbiddenFingerprint.ManualEntries ?? new List<AntiCheatManualFingerprintEntry>()).Where((AntiCheatManualFingerprintEntry x) => x != null).Take(32).ToList();
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (AntiCheatManualFingerprintEntry manualEntry in forbiddenFingerprint.ManualEntries)
			{
				string text2 = (manualEntry.Id ?? "").Trim();
				if (text2.Length == 0 || !hashSet2.Add(text2))
				{
					text2 = Guid.NewGuid().ToString("N");
					hashSet2.Add(text2);
				}
				manualEntry.Id = text2;
				manualEntry.Label = Clean(manualEntry.Label, 80, "Manual fingerprint");
				manualEntry.Version = Clean(manualEntry.Version, 64, "");
				manualEntry.FileName = Clean(manualEntry.FileName, 160, "");
				manualEntry.Notes = Clean(manualEntry.Notes, 300, "");
				manualEntry.PackageSha256 = NormalizeHash(manualEntry.PackageSha256);
				manualEntry.ContentSha256 = NormalizeHash(manualEntry.ContentSha256);
				manualEntry.AssemblySha256s = (from x in (manualEntry.AssemblySha256s ?? new List<string>()).Select(NormalizeHash)
					where x.Length == 64
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
				manualEntry.AssemblyMvids = (from x in manualEntry.AssemblyMvids ?? new List<string>()
					where Guid.TryParse(x, out var _)
					select Guid.Parse(x).ToString("D")).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
				manualEntry.AssemblyStructureSha256s = (from x in (manualEntry.AssemblyStructureSha256s ?? new List<string>()).Select(NormalizeHash)
					where x.Length == 64
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
				manualEntry.AssemblyBehaviorSha256s = (from x in (manualEntry.AssemblyBehaviorSha256s ?? new List<string>()).Select(NormalizeHash)
					where x.Length == 64
					select x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
				if (manualEntry.CreatedUtc < 0)
				{
					manualEntry.CreatedUtc = 0L;
				}
				if (manualEntry.UpdatedUtc < 0)
				{
					manualEntry.UpdatedUtc = 0L;
				}
			}
			forbiddenFingerprint.ManualEntries = forbiddenFingerprint.ManualEntries.Where((AntiCheatManualFingerprintEntry entry) => entry.PackageSha256.Length == 64 || entry.ContentSha256.Length == 64 || entry.AssemblySha256s.Count > 0 || entry.AssemblyMvids.Count > 0 || entry.AssemblyStructureSha256s.Count > 0 || entry.AssemblyBehaviorSha256s.Count > 0).ToList();
			forbiddenFingerprint.Releases = (forbiddenFingerprint.Releases ?? new List<AntiCheatReleaseFingerprint>()).Where((AntiCheatReleaseFingerprint x) => x != null).Take(6).ToList();
			foreach (AntiCheatReleaseFingerprint release in forbiddenFingerprint.Releases)
			{
				release.Version = Clean(release.Version, 64, "");
				release.FileName = Clean(release.FileName, 160, "");
				release.PackageSha256 = NormalizeHash(release.PackageSha256);
				release.ContentSha256 = NormalizeHash(release.ContentSha256);
				if (release.ResolvedUtc < 0)
				{
					release.ResolvedUtc = 0L;
				}
				release.Assemblies = (release.Assemblies ?? new List<AntiCheatReleaseAssemblyFingerprint>()).Where((AntiCheatReleaseAssemblyFingerprint x) => x != null).Take(32).ToList();
				foreach (AntiCheatReleaseAssemblyFingerprint assembly in release.Assemblies)
				{
					assembly.AssemblyName = Clean(assembly.AssemblyName, 160, "");
					assembly.Sha256 = NormalizeHash(assembly.Sha256);
					assembly.StructureTokenCount = Math.Clamp(assembly.StructureTokenCount, 0, 4096);
					assembly.SimilarityMinHash = (assembly.SimilarityMinHash ?? new List<ulong>()).Take(64).ToList();
					assembly.BehaviorSha256 = NormalizeHash(assembly.BehaviorSha256);
					assembly.BehaviorTokenCount = Math.Clamp(assembly.BehaviorTokenCount, 0, 4096);
					assembly.BehaviorMinHash = (assembly.BehaviorMinHash ?? new List<ulong>()).Take(64).ToList();
				}
			}
		}
		ConsequenceRules = ConsequenceRules.Where((AntiCheatConsequenceRule x) => x != null).Take(64).ToList();
		HashSet<string> hashSet3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AntiCheatConsequenceRule consequenceRule in ConsequenceRules)
		{
			string text3 = (consequenceRule.Id ?? "").Trim();
			if (text3.Length == 0 || !hashSet3.Add(text3))
			{
				text3 = Guid.NewGuid().ToString("N");
				hashSet3.Add(text3);
			}
			consequenceRule.Id = text3;
			consequenceRule.Name = Clean(consequenceRule.Name, 80, "Consequence");
			consequenceRule.TriggerType = NormalizeChoice(consequenceRule.TriggerType, "FindingCode", "Status", "RiskAtLeast");
			consequenceRule.TriggerValue = Clean(consequenceRule.TriggerValue, 80, (consequenceRule.TriggerType == "RiskAtLeast") ? "50" : "FORBIDDEN_MOD");
			consequenceRule.Subject = Clean(consequenceRule.Subject, 80, "");
			consequenceRule.Action = NormalizeChoice(consequenceRule.Action, "Log", "Kick", "TempBan", "Freeze");
			consequenceRule.Duration = Clean(consequenceRule.Duration, 24, "30m");
			consequenceRule.Message = Clean(consequenceRule.Message, 240, "Anti-cheat policy matched for {player}.");
		}
		HashSet<string> seenRules = new HashSet<string>(StringComparer.Ordinal);
		ConsequenceRules = ConsequenceRules.Where((AntiCheatConsequenceRule rule) => seenRules.Add(ConsequenceSemanticKey(rule))).ToList();
	}

	private static string ConsequenceSemanticKey(AntiCheatConsequenceRule rule)
	{
		return string.Join("\u001f", new string[8]
		{
			rule.Name ?? "",
			rule.Enabled ? "1" : "0",
			rule.TriggerType ?? "",
			rule.TriggerValue ?? "",
			rule.Subject ?? "",
			rule.Action ?? "",
			rule.Duration ?? "",
			rule.Message ?? ""
		});
	}

	private static List<string> NormalizeIds(IEnumerable<string> values)
	{
		return (from x in values
			where !string.IsNullOrWhiteSpace(x)
			select x.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static string NormalizeHash(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		return text.All(Uri.IsHexDigit) ? text : "";
	}

	private static string NormalizeChoice(string? value, params string[] choices)
	{
		string a = (value ?? "").Trim();
		foreach (string text in choices)
		{
			if (string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
			{
				return text;
			}
		}
		return choices[0];
	}

	private static string Clean(string? value, int max, string fallback)
	{
		string text = (value ?? "").Trim();
		if (text.Length == 0)
		{
			return fallback;
		}
		return (text.Length <= max) ? text : text.Substring(0, max);
	}
}
