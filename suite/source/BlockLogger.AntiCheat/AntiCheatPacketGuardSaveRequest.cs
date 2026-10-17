namespace BlockLogger.AntiCheat;

public sealed class AntiCheatPacketGuardSaveRequest
{
	public bool? IngressEnabled { get; set; }

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

	public double ReachUseExtraTolerance { get; set; } = 0.2;

	public double ReachBreakExtraTolerance { get; set; } = 0.0;

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

	public double MiningCadenceMinExpectedMs { get; set; } = 250.0;

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
