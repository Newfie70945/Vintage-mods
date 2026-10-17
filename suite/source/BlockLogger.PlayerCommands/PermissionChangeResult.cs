namespace BlockLogger.PlayerCommands;

public sealed class PermissionChangeResult
{
	public bool Success { get; set; }

	public string Message { get; set; } = "";

	public PermissionsManagerSnapshot Snapshot { get; set; } = new PermissionsManagerSnapshot();
}
