using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.PlayerCommands;

public sealed class WarpDefinition
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

	private static List<string> NormalizeAccessList(IEnumerable<string>? values)
	{
		return (from v in values ?? Array.Empty<string>()
			select (v ?? "").Trim() into v
			where v.Length > 0
			select (v.Length > 64) ? v.Substring(0, 64) : v).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string v) => v, StringComparer.OrdinalIgnoreCase).ToList();
	}

	public void Clamp()
	{
		Name = WarpService.NormalizeWarpName(Name);
		DisplayName = (string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName.Trim());
		if (DisplayName.Length > 64)
		{
			DisplayName = DisplayName.Substring(0, 64);
		}
		CooldownSeconds = Math.Clamp(CooldownSeconds, -1, 86400);
		WarmupSeconds = Math.Clamp(WarmupSeconds, -1, 300);
		CostItemCode = (CostItemCode ?? "").Trim();
		CostQuantity = Math.Clamp(CostQuantity, 1, 65535);
		if (string.IsNullOrWhiteSpace(CostItemCode))
		{
			CostEnabled = false;
		}
		AccessMode = (string.Equals(AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "any");
		AllowedRoles = NormalizeAccessList(AllowedRoles);
		RequiredPrivileges = NormalizeAccessList(RequiredPrivileges);
	}
}
