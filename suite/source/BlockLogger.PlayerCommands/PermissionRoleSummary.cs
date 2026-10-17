using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PermissionRoleSummary
{
	public string Code { get; set; } = "";

	public string Name { get; set; } = "";

	public string Description { get; set; } = "";

	public int PrivilegeLevel { get; set; }

	public List<string> Privileges { get; set; } = new List<string>();
}
