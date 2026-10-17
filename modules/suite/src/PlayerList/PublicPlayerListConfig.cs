using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.PlayerList;

public sealed class PublicPlayerListConfig
{
    public const int CurrentSchemaVersion = 6;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Optional title displayed at the top of the companion Tab list.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Opacity of the custom non-blurred roster shell.</summary>
    public double BackgroundOpacity { get; set; } = 0.72;

    public bool ShowRoles { get; set; } = true;
    public bool ShowPing { get; set; } = true;
    public bool ShowSessionTime { get; set; } = true;

    /// <summary>Additional player-list UI scale on top of Vintage Story GUI scaling.</summary>
    public double LayoutScale { get; set; } = 1.0;

    /// <summary>Maximum player rows in one column before another column is added.</summary>
    public int RowsPerColumn { get; set; } = 8;

    /// <summary>Maximum number of roster columns.</summary>
    public int MaxColumns { get; set; } = 4;

    /// <summary>CenterTop, LeftTop or RightTop.</summary>
    public string Alignment { get; set; } = "CenterTop";

    public int OffsetX { get; set; } = 0;
    public int OffsetY { get; set; } = 22;

    public bool ShowPlayerInitials { get; set; } = true;
    public bool ShowCapacityBar { get; set; } = true;
    public bool ShowColumnHeaders { get; set; } = false;
    public bool CompactRows { get; set; } = false;
    public bool ShowPingBars { get; set; } = true;
    public bool ShowFooterHint { get; set; } = false;

    /// <summary>Hex accent used for the roster border/header, for example #D6AE63.</summary>
    public string AccentHex { get; set; } = "#D6AE63";

    /// <summary>When enabled, exact role name/code matches can override player-name colors in Suite chat and Player List.</summary>
    public bool UseRoleNameColors { get; set; } = false;

    /// <summary>Case-insensitive role name/code to #RRGGBB overrides. Example: supporter -> #FEC500.</summary>
    public Dictionary<string, string> RoleNameColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>When enabled, part of MaxClients is reserved for configured roles.</summary>
    public bool ReservedSlotsEnabled { get; set; } = false;

    /// <summary>Number of server slots kept available for configured reserved-slot roles.</summary>
    public int ReservedSlots { get; set; } = 0;

    /// <summary>Case-insensitive Vintage Story role names/codes that may use reserved capacity.</summary>
    public List<string> ReservedSlotRoles { get; set; } = new() { "admin" };

    /// <summary>Disconnect reason shown to non-reserved players when only reserved capacity remains.</summary>
    public string ReservedSlotsFullMessage { get; set; } = "Only reserved slots are left on this server.";

    /// <summary>Extra backend logging for player-list troubleshooting.</summary>
    public bool DebugLogging { get; set; } = false;

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        ServerName = (ServerName ?? "").Trim();
        if (ServerName.Length > 96) ServerName = ServerName[..96];

        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.30, 0.95);
        LayoutScale = Math.Clamp(LayoutScale, 0.80, 1.35);
        RowsPerColumn = Math.Clamp(RowsPerColumn, 4, 16);
        MaxColumns = Math.Clamp(MaxColumns, 1, 5);
        OffsetX = Math.Clamp(OffsetX, -1200, 1200);
        OffsetY = Math.Clamp(OffsetY, -100, 900);

        Alignment = NormalizeAlignment(Alignment);
        AccentHex = NormalizeHex(AccentHex, "#D6AE63");

        RoleNameColors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in RoleNameColors.Take(1024))
        {
            string role = (pair.Key ?? "").Trim();
            if (role.Length == 0) continue;
            if (role.Length > 64) role = role[..64];
            string color = NormalizeHex(pair.Value, "");
            if (color.Length == 7) cleaned[role] = color;
        }
        RoleNameColors = cleaned;

        ReservedSlots = Math.Clamp(ReservedSlots, 0, 256);
        ReservedSlotRoles ??= new List<string>();
        ReservedSlotRoles = ReservedSlotRoles
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Select(x => x.Length > 64 ? x[..64] : x)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToList();

        ReservedSlotsFullMessage = (ReservedSlotsFullMessage ?? "").Trim();
        if (ReservedSlotsFullMessage.Length == 0)
            ReservedSlotsFullMessage = "Only reserved slots are left on this server.";
        if (ReservedSlotsFullMessage.Length > 240)
            ReservedSlotsFullMessage = ReservedSlotsFullMessage[..240];
    }

    private static string NormalizeAlignment(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Equals("LeftTop", StringComparison.OrdinalIgnoreCase)) return "LeftTop";
        if (value.Equals("RightTop", StringComparison.OrdinalIgnoreCase)) return "RightTop";
        return "CenterTop";
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6) return fallback;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool hex = (c >= '0' && c <= '9') ||
                       (c >= 'a' && c <= 'f') ||
                       (c >= 'A' && c <= 'F');
            if (!hex) return fallback;
        }

        return "#" + text.ToUpperInvariant();
    }
}
