namespace BlockLogger.AntiCheat;

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
