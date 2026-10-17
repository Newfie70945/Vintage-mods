namespace BlockLogger.AntiCheat;

public sealed class AntiCheatEvidenceSettingsSaveRequest
{
	public bool Enabled { get; set; } = true;

	public int MaxMb { get; set; } = 96;

	public int TimeoutSeconds { get; set; } = 120;

	public int UploadKbps { get; set; } = 384;

	public string DeclineAction { get; set; } = "TempBan";

	public int DeclineTempBanHours { get; set; } = 24;
}
