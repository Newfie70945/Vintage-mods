namespace BlockLogger.ModUpdates;

public sealed class ModUpdateApplyResult
{
	public bool Success { get; set; }

	public string ModId { get; set; } = "";

	public string InstalledVersion { get; set; } = "";

	public string NewVersion { get; set; } = "";

	public string Message { get; set; } = "";

	public string BackupPath { get; set; } = "";

	public string InstalledPath { get; set; } = "";

	public string ModPageUrl { get; set; } = "";

	public bool RestartRequired { get; set; }
}
