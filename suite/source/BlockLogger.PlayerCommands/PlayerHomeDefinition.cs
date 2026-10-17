namespace BlockLogger.PlayerCommands;

public sealed class PlayerHomeDefinition
{
	public string Name { get; set; } = "home";

	public PlayerTeleportPosition Position { get; set; } = new PlayerTeleportPosition();

	public long UpdatedAtUnixMilliseconds { get; set; }
}
