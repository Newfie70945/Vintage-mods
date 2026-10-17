using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class WarpSnapshot
{
	public bool Available { get; set; } = true;

	public bool Enabled { get; set; }

	public int DefaultCooldownSeconds { get; set; }

	public int DefaultWarmupSeconds { get; set; }

	public bool DefaultCancelOnMove { get; set; }

	public List<WarpDefinition> Warps { get; set; } = new List<WarpDefinition>();

	public List<string> AvailableRoles { get; set; } = new List<string>();

	public List<string> AvailablePrivileges { get; set; } = new List<string>();
}
