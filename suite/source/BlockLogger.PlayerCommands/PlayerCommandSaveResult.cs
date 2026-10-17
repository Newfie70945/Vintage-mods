namespace BlockLogger.PlayerCommands;

public sealed class PlayerCommandSaveResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public PlayerCommandSnapshot Snapshot { get; set; } = new PlayerCommandSnapshot();
}
