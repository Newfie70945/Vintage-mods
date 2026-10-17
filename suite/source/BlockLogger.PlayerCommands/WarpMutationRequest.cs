using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class WarpMutationRequest
{
	public string Name { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public bool Enabled { get; set; } = true;

	public double X { get; set; }

	public double Y { get; set; }

	public double Z { get; set; }

	public int CooldownSeconds { get; set; } = -1;

	public int WarmupSeconds { get; set; } = -1;

	public bool? CancelOnMove { get; set; }

	public bool CostEnabled { get; set; }

	public string CostItemCode { get; set; } = "";

	public int CostQuantity { get; set; } = 1;

	public string AccessMode { get; set; } = "any";

	public List<string> AllowedRoles { get; set; } = new List<string>();

	public List<string> RequiredPrivileges { get; set; } = new List<string>();
}
