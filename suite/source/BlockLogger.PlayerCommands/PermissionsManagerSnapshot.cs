using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PermissionsManagerSnapshot
{
	public List<PermissionRoleSummary> Roles { get; set; } = new List<PermissionRoleSummary>();

	public List<PermissionPlayerSummary> OnlinePlayers { get; set; } = new List<PermissionPlayerSummary>();

	public List<string> KnownPrivileges { get; set; } = new List<string>();
}
