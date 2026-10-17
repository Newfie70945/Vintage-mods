using System.Collections.Generic;
using BlockLogger.PlayerCommands;

namespace BlockLogger.Roles;

public sealed class RoleEditorRow
{
	public string Code { get; set; } = "";

	public string Name { get; set; } = "";

	public string Description { get; set; } = "";

	public int PrivilegeLevel { get; set; }

	public string ColorHex { get; set; } = "#FFFFFF";

	public string NameColorHex { get; set; } = "";

	public bool AutoGrant { get; set; }

	public string DefaultGameMode { get; set; } = "Survival";

	public int LandClaimAllowance { get; set; }

	public int LandClaimMaxAreas { get; set; }

	public int ClaimMinX { get; set; } = 5;

	public int ClaimMinY { get; set; } = 5;

	public int ClaimMinZ { get; set; } = 5;

	public List<string> Privileges { get; set; } = new List<string>();

	public List<string> RuntimePrivileges { get; set; } = new List<string>();

	public int? HomeLimit { get; set; }

	public Dictionary<string, TeleportRolePolicy?> Policies { get; set; } = new Dictionary<string, TeleportRolePolicy>();

	public int AssignedPlayers { get; set; }

	public bool IsDefault { get; set; }
}
