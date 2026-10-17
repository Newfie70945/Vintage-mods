namespace BlockLogger.PlayerCommands;

public sealed class PermissionChangeRequest
{
	public string PlayerUid { get; set; } = "";

	public string Action { get; set; } = "";

	public string Value { get; set; } = "";
}
