namespace BlockLogger.PlayerCommands;

public sealed class WarpSettingsRequest
{
	public bool Enabled { get; set; } = true;

	public int DefaultCooldownSeconds { get; set; } = 60;

	public int DefaultWarmupSeconds { get; set; } = 5;

	public bool DefaultCancelOnMove { get; set; } = true;
}
