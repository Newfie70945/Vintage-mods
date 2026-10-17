using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AntiCheatPacketProtectionGuiPacket
{
	[ProtoMember(1)]
	public bool PacketGuardEnabled { get; set; } = true;

	[ProtoMember(2)]
	public bool PlayerWarningEnabled { get; set; } = false;

	[ProtoMember(3)]
	public int PlayerWarningCooldownSeconds { get; set; } = 15;

	[ProtoMember(4)]
	public int AccelerationSampleMinMs { get; set; } = 220;

	[ProtoMember(5)]
	public double AccelerationSmoothingFactor { get; set; } = 0.25;

	[ProtoMember(6)]
	public bool NoclipEnabled { get; set; } = false;

	[ProtoMember(7)]
	public int NoclipRequiredCollisionSamples { get; set; } = 3;

	[ProtoMember(8)]
	public double NoclipMinPenetrationDepth { get; set; } = 0.18;

	[ProtoMember(9)]
	public double NoclipEndpointMinNetMovement { get; set; } = 0.45;

	[ProtoMember(10)]
	public double MountAccelerationMinSpeedRatio { get; set; } = 0.85;

	[ProtoMember(11)]
	public bool DiagnosticContextEnabled { get; set; } = true;

	[ProtoMember(12)]
	public bool DiagnosticSuppressionEnabled { get; set; } = true;

	[ProtoMember(13)]
	public int DiagnosticSpeedHistorySamples { get; set; } = 8;

	[ProtoMember(14)]
	public bool MovementEnabled { get; set; } = true;

	[ProtoMember(15)]
	public int MovementSampleMs { get; set; } = 75;

	[ProtoMember(16)]
	public double HardMoveDistance { get; set; } = 32.0;

	[ProtoMember(17)]
	public int MovementConfirmSamples { get; set; } = 2;

	[ProtoMember(18)]
	public int MovementConfirmWindowMs { get; set; } = 600;

	[ProtoMember(19)]
	public double MovementExtremeMultiplier { get; set; } = 2.75;

	[ProtoMember(20)]
	public int CorrectionGraceMs { get; set; } = 1200;

	[ProtoMember(21)]
	public bool RejectUnauthorizedMovement { get; set; } = true;

	[ProtoMember(22)]
	public bool ExemptAdmins { get; set; } = true;

	[ProtoMember(23)]
	public bool ExemptMountedPlayers { get; set; } = true;

	[ProtoMember(24)]
	public int MountGraceMs { get; set; } = 2000;

	[ProtoMember(25)]
	public int EngineTeleportGraceMs { get; set; } = 5000;

	[ProtoMember(26)]
	public bool TPRunesCompatibility { get; set; } = true;

	[ProtoMember(27)]
	public int TPRunesGraceMs { get; set; } = 8000;

	[ProtoMember(28)]
	public bool TPRunesDetected { get; set; }

	[ProtoMember(29)]
	public bool Th3EssentialsSpawnCompatibility { get; set; } = true;

	[ProtoMember(30)]
	public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;

	[ProtoMember(31)]
	public bool Th3EssentialsDetected { get; set; }

	[ProtoMember(32)]
	public bool ReachEnabled { get; set; } = true;

	[ProtoMember(33)]
	public double ReachTolerance { get; set; } = 1.25;

	[ProtoMember(34)]
	public bool ReachLatencyCompensation { get; set; } = true;

	[ProtoMember(35)]
	public double ReachMaxLatencyCompensation { get; set; } = 1.25;

	[ProtoMember(36)]
	public bool ReachPositionHistoryEnabled { get; set; } = true;

	[ProtoMember(37)]
	public int ReachPositionHistoryMs { get; set; } = 650;

	[ProtoMember(38)]
	public int ReachMaxRewindMs { get; set; } = 350;

	[ProtoMember(39)]
	public double ReachUseExtraTolerance { get; set; } = 0.2;

	[ProtoMember(40)]
	public double ReachBreakExtraTolerance { get; set; } = 0.0;

	[ProtoMember(41)]
	public int ReachNearThresholdStrikes { get; set; } = 3;

	[ProtoMember(42)]
	public int ReachStrikeWindowMs { get; set; } = 3500;

	[ProtoMember(43)]
	public int ReachStrikeDecayMs { get; set; } = 1800;

	[ProtoMember(44)]
	public double ReachHardExcess { get; set; } = 1.5;

	[ProtoMember(45)]
	public bool RejectOutOfReachActions { get; set; } = true;

	[ProtoMember(46)]
	public bool MiningEnabled { get; set; } = true;

	[ProtoMember(47)]
	public int VeinWindowMs { get; set; } = 2200;

	[ProtoMember(48)]
	public int ConnectedBreakLimit { get; set; } = 18;

	[ProtoMember(49)]
	public int MiningDuplicateTargetMs { get; set; } = 450;

	[ProtoMember(50)]
	public bool RejectConnectedBreakBursts { get; set; } = true;

	[ProtoMember(51)]
	public bool MiningCadenceValidationEnabled { get; set; } = false;

	[ProtoMember(52)]
	public double MiningCadenceMinExpectedMs { get; set; } = 250.0;

	[ProtoMember(53)]
	public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;

	[ProtoMember(54)]
	public int MiningCadenceConfirmations { get; set; } = 3;

	[ProtoMember(55)]
	public int MiningCadenceConfirmationWindowMs { get; set; } = 8000;

	[ProtoMember(56)]
	public bool RejectImpossibleMiningCadence { get; set; } = false;

	[ProtoMember(57)]
	public bool TpsAwarenessEnabled { get; set; } = true;

	[ProtoMember(58)]
	public double TpsDegradedPercent { get; set; } = 85.0;

	[ProtoMember(59)]
	public double TpsCriticalPercent { get; set; } = 60.0;

	[ProtoMember(60)]
	public double TpsMaxTimingMultiplier { get; set; } = 2.5;

	[ProtoMember(61)]
	public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;

	[ProtoMember(62)]
	public bool TpsReady { get; set; }

	[ProtoMember(63)]
	public double TpsTarget { get; set; }

	[ProtoMember(64)]
	public double TpsEstimated { get; set; }

	[ProtoMember(65)]
	public double TpsHealthPercent { get; set; }

	[ProtoMember(66)]
	public string TpsBand { get; set; } = "Disabled";

	[ProtoMember(67)]
	public bool PerFamilyRiskEnabled { get; set; } = true;

	[ProtoMember(68)]
	public int MovementRiskDecaySeconds { get; set; } = 180;

	[ProtoMember(69)]
	public int MovementRiskReviewThreshold { get; set; } = 25;

	[ProtoMember(70)]
	public int MovementRiskSuspiciousThreshold { get; set; } = 50;

	[ProtoMember(71)]
	public int ReachRiskDecaySeconds { get; set; } = 300;

	[ProtoMember(72)]
	public int ReachRiskReviewThreshold { get; set; } = 25;

	[ProtoMember(73)]
	public int ReachRiskSuspiciousThreshold { get; set; } = 50;

	[ProtoMember(74)]
	public int MiningRiskDecaySeconds { get; set; } = 300;

	[ProtoMember(75)]
	public int MiningRiskReviewThreshold { get; set; } = 25;

	[ProtoMember(76)]
	public int MiningRiskSuspiciousThreshold { get; set; } = 50;

	[ProtoMember(77)]
	public int PacketAbuseRiskDecaySeconds { get; set; } = 600;

	[ProtoMember(78)]
	public int PacketAbuseRiskReviewThreshold { get; set; } = 25;

	[ProtoMember(79)]
	public int PacketAbuseRiskSuspiciousThreshold { get; set; } = 50;

	[ProtoMember(80)]
	public int ClientIntegrityRiskDecaySeconds { get; set; } = 1800;

	[ProtoMember(81)]
	public int ClientIntegrityRiskReviewThreshold { get; set; } = 10;

	[ProtoMember(82)]
	public int ClientIntegrityRiskSuspiciousThreshold { get; set; } = 50;

	[ProtoMember(83)]
	public bool IncidentCorrelationEnabled { get; set; } = true;

	[ProtoMember(84)]
	public int IncidentCorrelationWindowSeconds { get; set; } = 90;

	[ProtoMember(85)]
	public int IncidentCorrelationRequiredCategories { get; set; } = 2;

	[ProtoMember(86)]
	public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;

	[ProtoMember(87)]
	public bool HistoryEnabled { get; set; } = true;

	[ProtoMember(88)]
	public int HistoryRetentionDays { get; set; } = 90;

	[ProtoMember(89)]
	public int HistoryMaxRecords { get; set; } = 500;

	[ProtoMember(90)]
	public bool IngressEnabled { get; set; }

	[ProtoMember(91)]
	public bool IngressActive { get; set; }

	[ProtoMember(92)]
	public string IngressStatus { get; set; } = "Unknown";
}
