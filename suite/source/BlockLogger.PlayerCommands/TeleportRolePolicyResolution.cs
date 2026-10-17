namespace BlockLogger.PlayerCommands;

public sealed class TeleportRolePolicyResolution
{
	public bool Allowed { get; init; } = true;

	public int CooldownSeconds { get; init; }

	public string RoleCode { get; init; } = "";

	public bool Overridden { get; init; }
}
