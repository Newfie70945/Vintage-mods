using System.Collections.Generic;

namespace BlockLogger.AntiCheat;

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

	public List<AntiCheatTrustedTeleportSourceWebSnapshot> TrustedTeleportSources { get; set; } = new List<AntiCheatTrustedTeleportSourceWebSnapshot>();

	public List<AntiCheatPacketSuppressionWebSnapshot> PacketSuppressions { get; set; } = new List<AntiCheatPacketSuppressionWebSnapshot>();
}
