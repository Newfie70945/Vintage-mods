namespace BlockLogger.AntiCheat;

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

	public double HardMoveDistance { get; set; } = 32.0;

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

	public double TpsDegradedPercent { get; set; } = 85.0;

	public double TpsCriticalPercent { get; set; } = 60.0;

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
