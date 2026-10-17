using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PermissionPlayerSummary
{
	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public string Nickname { get; set; } = "";

	public string RoleCode { get; set; } = "";

	public List<string> EffectivePrivileges { get; set; } = new List<string>();

	public List<string> PermanentPrivileges { get; set; } = new List<string>();

	public List<string> DeniedPrivileges { get; set; } = new List<string>();
}
