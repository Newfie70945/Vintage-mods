using System.Collections.Generic;
using BlockLogger.PlayerCommands;

namespace BlockLogger.Roles;

public sealed class RoleEditorSnapshot
{
	public bool Available { get; set; }

	public bool PoliciesAvailable { get; set; }

	public bool ColorsAvailable { get; set; }

	public bool UseRoleNameColors { get; set; }

	public string Revision { get; set; } = "";

	public int GlobalHomeLimit { get; set; }

	public List<string> KnownPrivileges { get; set; } = new List<string>();

	public List<string> GameModes { get; set; } = new List<string>();

	public Dictionary<string, int> DefaultCooldowns { get; set; } = new Dictionary<string, int>();

	public Dictionary<string, TeleportRolePolicy> WildcardPolicies { get; set; } = new Dictionary<string, TeleportRolePolicy>();

	public List<RoleEditorRow> Roles { get; set; } = new List<RoleEditorRow>();
}
