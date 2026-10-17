using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AntiCheatGuiSettingsPacket
{
	[ProtoMember(1)]
	public bool RequireClient { get; set; } = true;

	[ProtoMember(2)]
	public int RescanMinutes { get; set; } = 15;

	[ProtoMember(3)]
	public bool RuntimeRescanOnChange { get; set; } = true;

	[ProtoMember(4)]
	public int RuntimeChangeRescanCooldownSeconds { get; set; } = 30;

	[ProtoMember(5)]
	public bool SimilarityDetectionEnabled { get; set; } = true;

	[ProtoMember(6)]
	public int SimilarityReviewThresholdPercent { get; set; } = 72;

	[ProtoMember(7)]
	public int SimilarityHighThresholdPercent { get; set; } = 88;

	[ProtoMember(8)]
	public bool PacketGuardEnabled { get; set; } = true;

	[ProtoMember(9)]
	public bool MovementEnabled { get; set; } = true;

	[ProtoMember(10)]
	public double HardMoveDistance { get; set; } = 32.0;

	[ProtoMember(11)]
	public bool RejectUnauthorizedMovement { get; set; } = true;

	[ProtoMember(12)]
	public bool ReachEnabled { get; set; } = true;

	[ProtoMember(13)]
	public double ReachTolerance { get; set; } = 1.25;

	[ProtoMember(14)]
	public double ReachHardExcess { get; set; } = 1.5;

	[ProtoMember(15)]
	public bool RejectOutOfReachActions { get; set; } = true;

	[ProtoMember(16)]
	public bool MiningEnabled { get; set; } = true;

	[ProtoMember(17)]
	public bool MiningCadenceValidationEnabled { get; set; } = false;

	[ProtoMember(18)]
	public double MiningCadenceSuspiciousRatio { get; set; } = 0.38;

	[ProtoMember(19)]
	public int MiningCadenceConfirmations { get; set; } = 3;

	[ProtoMember(20)]
	public bool RejectConnectedBreakBursts { get; set; } = true;

	[ProtoMember(21)]
	public bool RejectImpossibleMiningCadence { get; set; }

	[ProtoMember(22)]
	public bool TpsAwarenessEnabled { get; set; } = true;

	[ProtoMember(23)]
	public double TpsDegradedPercent { get; set; } = 85.0;

	[ProtoMember(24)]
	public double TpsCriticalPercent { get; set; } = 60.0;

	[ProtoMember(25)]
	public double TpsMaxTimingMultiplier { get; set; } = 2.5;

	[ProtoMember(26)]
	public bool TpsSuppressTimingRejectsWhenCritical { get; set; } = true;

	[ProtoMember(27)]
	public bool IncidentCorrelationEnabled { get; set; } = true;

	[ProtoMember(28)]
	public int IncidentCorrelationWindowSeconds { get; set; } = 90;

	[ProtoMember(29)]
	public int IncidentCorrelationRequiredCategories { get; set; } = 2;

	[ProtoMember(30)]
	public int IncidentCorrelationMaxRiskBonus { get; set; } = 20;

	[ProtoMember(31)]
	public bool HistoryEnabled { get; set; } = true;

	[ProtoMember(32)]
	public int HistoryRetentionDays { get; set; } = 90;

	[ProtoMember(33)]
	public int HistoryMaxRecords { get; set; } = 500;

	[ProtoMember(34)]
	public bool Th3EssentialsSpawnCompatibility { get; set; } = true;

	[ProtoMember(35)]
	public int Th3EssentialsSpawnGraceMs { get; set; } = 8000;

	[ProtoMember(36)]
	public bool Th3EssentialsDetected { get; set; }
}
