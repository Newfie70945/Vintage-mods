using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.PlayerList;

public sealed class PublicPlayerListConfig
{
	public const int CurrentSchemaVersion = 6;

	public int SchemaVersion { get; set; } = 6;

	public string ServerName { get; set; } = "";

	public double BackgroundOpacity { get; set; } = 0.72;

	public bool ShowRoles { get; set; } = true;

	public bool ShowPing { get; set; } = true;

	public bool ShowSessionTime { get; set; } = true;

	public double LayoutScale { get; set; } = 1.0;

	public int RowsPerColumn { get; set; } = 8;

	public int MaxColumns { get; set; } = 4;

	public string Alignment { get; set; } = "CenterTop";

	public int OffsetX { get; set; } = 0;

	public int OffsetY { get; set; } = 22;

	public bool ShowPlayerInitials { get; set; } = true;

	public bool ShowCapacityBar { get; set; } = true;

	public bool ShowColumnHeaders { get; set; } = false;

	public bool CompactRows { get; set; } = false;

	public bool ShowPingBars { get; set; } = true;

	public bool ShowFooterHint { get; set; } = false;

	public string AccentHex { get; set; } = "#D6AE63";

	public bool UseRoleNameColors { get; set; } = false;

	public Dictionary<string, string> RoleNameColors { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public bool ReservedSlotsEnabled { get; set; } = false;

	public int ReservedSlots { get; set; } = 0;

	public List<string> ReservedSlotRoles { get; set; } = new List<string> { "admin" };

	public string ReservedSlotsFullMessage { get; set; } = "Only reserved slots are left on this server.";

	public bool DebugLogging { get; set; } = false;

	public void Clamp()
	{
		SchemaVersion = Math.Max(6, SchemaVersion);
		ServerName = (ServerName ?? "").Trim();
		if (ServerName.Length > 96)
		{
			ServerName = ServerName.Substring(0, 96);
		}
		BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.3, 0.95);
		LayoutScale = Math.Clamp(LayoutScale, 0.8, 1.35);
		RowsPerColumn = Math.Clamp(RowsPerColumn, 4, 16);
		MaxColumns = Math.Clamp(MaxColumns, 1, 5);
		OffsetX = Math.Clamp(OffsetX, -1200, 1200);
		OffsetY = Math.Clamp(OffsetY, -100, 900);
		Alignment = NormalizeAlignment(Alignment);
		AccentHex = NormalizeHex(AccentHex, "#D6AE63");
		if (RoleNameColors == null)
		{
			Dictionary<string, string> dictionary = (RoleNameColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
		}
		Dictionary<string, string> dictionary3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, string> item in RoleNameColors.Take(1024))
		{
			string text = (item.Key ?? "").Trim();
			if (text.Length != 0)
			{
				if (text.Length > 64)
				{
					text = text.Substring(0, 64);
				}
				string text2 = NormalizeHex(item.Value, "");
				if (text2.Length == 7)
				{
					dictionary3[text] = text2;
				}
			}
		}
		RoleNameColors = dictionary3;
		ReservedSlots = Math.Clamp(ReservedSlots, 0, 256);
		if (ReservedSlotRoles == null)
		{
			List<string> list = (ReservedSlotRoles = new List<string>());
		}
		ReservedSlotRoles = (from x in ReservedSlotRoles
			select (x ?? "").Trim() into x
			where x.Length > 0
			select (x.Length > 64) ? x.Substring(0, 64) : x).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
		ReservedSlotsFullMessage = (ReservedSlotsFullMessage ?? "").Trim();
		if (ReservedSlotsFullMessage.Length == 0)
		{
			ReservedSlotsFullMessage = "Only reserved slots are left on this server.";
		}
		if (ReservedSlotsFullMessage.Length > 240)
		{
			ReservedSlotsFullMessage = ReservedSlotsFullMessage.Substring(0, 240);
		}
	}

	private static string NormalizeAlignment(string? value)
	{
		value = (value ?? "").Trim();
		if (value.Equals("LeftTop", StringComparison.OrdinalIgnoreCase))
		{
			return "LeftTop";
		}
		if (value.Equals("RightTop", StringComparison.OrdinalIgnoreCase))
		{
			return "RightTop";
		}
		return "CenterTop";
	}

	private static string NormalizeHex(string? value, string fallback)
	{
		string text = (value ?? "").Trim();
		if (text.StartsWith('#'))
		{
			text = text.Substring(1);
		}
		if (text.Length != 6)
		{
			return fallback;
		}
		foreach (char c in text)
		{
			if ((c < '0' || c > '9') && (c < 'a' || c > 'f') && (c < 'A' || c > 'F'))
			{
				return fallback;
			}
		}
		return "#" + text.ToUpperInvariant();
	}
}
