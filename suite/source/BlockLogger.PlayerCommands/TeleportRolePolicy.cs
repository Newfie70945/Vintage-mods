namespace BlockLogger.PlayerCommands;

public sealed class TeleportRolePolicy
{
	public bool Allowed { get; set; } = true;

	public int CooldownSeconds { get; set; } = -1;
}
