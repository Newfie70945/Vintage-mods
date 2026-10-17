using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace NewfiesPlayerList;

/// <summary>
/// Custom non-blurred roster surface. Everything is drawn in one Cairo element so the
/// player list can have a polished layered look without the partial blur path that caused
/// the earlier GuiElementDialogBackground crash.
/// </summary>
public sealed class PlayerRosterElement : GuiElement
{
    private readonly ICoreClientAPI capi;
    private readonly PublicPlayerListResponse snapshot;
    private readonly bool loading;
    private readonly string statusMessage;
    private readonly List<PublicPlayerListEntry> players;
    private readonly TextDrawUtil text = new();

    private readonly CairoFont titleFont;
    private readonly CairoFont statFont;
    private readonly CairoFont nameFont;
    private readonly CairoFont detailFont;
    private readonly CairoFont tinyFont;
    private readonly CairoFont mutedFont;
    private readonly CairoFont dateYearFont;
    private readonly CairoFont dateDetailFont;
    private readonly CairoFont adminFont;
    private readonly CairoFont staffFont;
    private readonly CairoFont normalRoleFont;
    private readonly CairoFont pingGoodFont;
    private readonly CairoFont pingFairFont;
    private readonly CairoFont pingPoorFont;
    private readonly CairoFont pingBadFont;
    private readonly Dictionary<string, CairoFont> nameColorFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CairoFont> detailColorFonts = new(StringComparer.OrdinalIgnoreCase);

    private readonly double uiScale;
    private readonly int rowsPerColumn;
    private readonly int maxColumns;
    private readonly bool compactRows;
    private readonly bool showInitials;
    private readonly bool showCapacityBar;
    private readonly bool showColumnHeaders;
    private readonly bool showPingBars;
    private readonly bool showFooterHint;
    private readonly bool showRoles;
    private readonly bool showPing;
    private readonly bool showSession;
    private readonly double opacity;
    private readonly double[] accent;

    private readonly int renderCount;
    private readonly int columns;
    private readonly int rows;
    private readonly double cellWidth;
    private readonly double rowHeight;
    private readonly double gap;
    private readonly double pad;
    private readonly double headerHeight;
    private readonly double columnHeaderHeight;
    private readonly double footerHeight;

    public double PanelWidth { get; }
    public double PanelHeight { get; }

    public PlayerRosterElement(
        ICoreClientAPI capi,
        PublicPlayerListResponse snapshot,
        bool loading,
        string statusMessage,
        ElementBounds bounds) : base(capi, bounds)
    {
        this.capi = capi;
        this.snapshot = snapshot ?? new PublicPlayerListResponse();
        this.loading = loading;
        this.statusMessage = statusMessage ?? "";
        players = this.snapshot.Players ?? new List<PublicPlayerListEntry>();

        uiScale = Math.Clamp(this.snapshot.LayoutScale <= 0 ? 1.0 : this.snapshot.LayoutScale, 0.80, 1.35);
        rowsPerColumn = Math.Clamp(this.snapshot.RowsPerColumn <= 0 ? 8 : this.snapshot.RowsPerColumn, 4, 16);
        maxColumns = Math.Clamp(this.snapshot.MaxColumns <= 0 ? 4 : this.snapshot.MaxColumns, 1, 5);
        compactRows = this.snapshot.CompactRows;
        showInitials = this.snapshot.ShowPlayerInitials;
        showCapacityBar = this.snapshot.ShowCapacityBar;
        showColumnHeaders = this.snapshot.ShowColumnHeaders;
        showPingBars = this.snapshot.ShowPingBars;
        showFooterHint = this.snapshot.ShowFooterHint;
        showRoles = this.snapshot.StyleVersion <= 0 || this.snapshot.ShowRoles;
        showPing = this.snapshot.StyleVersion <= 0 || this.snapshot.ShowPing;
        showSession = this.snapshot.StyleVersion < 3 || this.snapshot.ShowSessionTime;
        opacity = Math.Clamp(this.snapshot.BackgroundOpacity <= 0 ? 0.72 : this.snapshot.BackgroundOpacity, 0.30, 0.95);
        accent = ParseHex(this.snapshot.AccentHex, 0.84, 0.68, 0.39);

        cellWidth = 348 * uiScale;
        rowHeight = (compactRows ? 42 : 52) * uiScale;
        gap = 10 * uiScale;
        pad = 14 * uiScale;
        headerHeight = 76 * uiScale;
        columnHeaderHeight = showColumnHeaders ? 20 * uiScale : 0;

        int maxVisible = rowsPerColumn * maxColumns;
        renderCount = Math.Min(players.Count, maxVisible);
        columns = Math.Max(1, Math.Min(maxColumns, (int)Math.Ceiling(Math.Max(1, renderCount) / (double)rowsPerColumn)));
        rows = Math.Max(1, (int)Math.Ceiling(Math.Max(1, renderCount) / (double)columns));

        bool needsFooter = showFooterHint || players.Count > renderCount;
        footerHeight = needsFooter ? 25 * uiScale : 10 * uiScale;

        double bodyHeight = loading || !string.IsNullOrWhiteSpace(this.statusMessage) || renderCount == 0
            ? 62 * uiScale
            : rows * rowHeight + Math.Max(0, rows - 1) * (2 * uiScale);

        PanelWidth = pad * 2 + columns * cellWidth + Math.Max(0, columns - 1) * gap;
        PanelHeight = headerHeight + columnHeaderHeight + bodyHeight + footerHeight + pad;
        Bounds.fixedWidth = PanelWidth;
        Bounds.fixedHeight = PanelHeight;

        titleFont = CairoFont.WhiteMediumText().WithFontSize((float)(21 * uiScale)).WithWeight(FontWeight.Bold);
        statFont = CairoFont.WhiteSmallText().WithFontSize((float)(12 * uiScale)).WithWeight(FontWeight.Bold).WithColor(new[] { 0.46, 0.84, 0.50, 1.0 });
        nameFont = CairoFont.WhiteSmallText().WithFontSize((float)(14 * uiScale)).WithWeight(FontWeight.Bold);
        detailFont = CairoFont.WhiteDetailText().WithFontSize((float)(11 * uiScale));
        tinyFont = CairoFont.WhiteDetailText().WithFontSize((float)(9.5 * uiScale));
        mutedFont = CairoFont.WhiteDetailText().WithFontSize((float)(10 * uiScale)).WithColor(new[] { 0.58, 0.62, 0.64, 1.0 });
        dateYearFont = CairoFont.WhiteDetailText().WithFontSize((float)(11.5 * uiScale)).WithWeight(FontWeight.Bold).WithColor(new[] { accent[0], accent[1], accent[2], 1.0 });
        dateDetailFont = CairoFont.WhiteDetailText().WithFontSize((float)(10.5 * uiScale)).WithColor(new[] { 0.74, 0.77, 0.78, 1.0 });
        adminFont = detailFont.Clone().WithColor(new[] { 0.93, 0.76, 0.42, 1.0 });
        staffFont = detailFont.Clone().WithColor(new[] { 0.49, 0.72, 0.90, 1.0 });
        normalRoleFont = detailFont.Clone().WithColor(new[] { 0.62, 0.66, 0.68, 1.0 });
        pingGoodFont = tinyFont.Clone().WithColor(new[] { 0.45, 0.84, 0.48, 1.0 });
        pingFairFont = tinyFont.Clone().WithColor(new[] { 0.85, 0.80, 0.38, 1.0 });
        pingPoorFont = tinyFont.Clone().WithColor(new[] { 0.91, 0.62, 0.34, 1.0 });
        pingBadFont = tinyFont.Clone().WithColor(new[] { 0.88, 0.40, 0.40, 1.0 });
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        Bounds.CalcWorldBounds();
        double x = Bounds.drawX;
        double y = Bounds.drawY;
        double width = Bounds.InnerWidth;
        double height = Bounds.InnerHeight;

        DrawPanel(ctx, x, y, width, height);
        DrawHeader(ctx, x, y, width);

        double bodyY = y + scaled(headerHeight);
        if (showColumnHeaders)
        {
            DrawColumnHeaders(ctx, x, bodyY, width);
            bodyY += scaled(columnHeaderHeight);
        }

        if (loading)
        {
            DrawCentered(ctx, mutedFont, "Loading player list…", x, bodyY + scaled(20 * uiScale), width);
        }
        else if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            DrawCentered(ctx, normalRoleFont, statusMessage, x + scaled(pad), bodyY + scaled(20 * uiScale), width - scaled(pad * 2));
        }
        else if (renderCount == 0)
        {
            DrawCentered(ctx, mutedFont, "No players are currently online.", x, bodyY + scaled(20 * uiScale), width);
        }
        else
        {
            DrawRoster(ctx, x, bodyY);
        }

        DrawFooter(ctx, x, y, width, height);
    }

    private void DrawPanel(Context ctx, double x, double y, double width, double height)
    {
        // Soft shadow.
        ctx.SetSourceRGBA(0, 0, 0, Math.Min(0.45, opacity * 0.55));
        RoundRectangle(ctx, x + scaled(2), y + scaled(3), width, height, scaled(7));
        ctx.Fill();

        // Dark neutral shell rather than Vintage Story's large brown dialog texture.
        ctx.SetSourceRGBA(0.055, 0.052, 0.048, opacity);
        RoundRectangle(ctx, x, y, width, height, scaled(7));
        ctx.Fill();

        // Fine outline and top accent.
        ctx.LineWidth = scaled(1);
        ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.42);
        RoundRectangle(ctx, x + scaled(0.5), y + scaled(0.5), width - scaled(1), height - scaled(1), scaled(7));
        ctx.Stroke();

        ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.90);
        RoundRectangle(ctx, x + scaled(12 * uiScale), y + scaled(7 * uiScale), width - scaled(24 * uiScale), scaled(2.5 * uiScale), scaled(1.2));
        ctx.Fill();
    }

    private void DrawHeader(Context ctx, double x, double y, double width)
    {
        double left = x + scaled(pad);
        double top = y + scaled(17 * uiScale);
        string title = string.IsNullOrWhiteSpace(snapshot.ServerName) ? "Vintage Story Server" : snapshot.ServerName.Trim();

        DrawText(ctx, titleFont, title, left, top);

        if (TryFormatInGameDate(out string yearLabel, out string dateDetail))
        {
            double dateY = top + scaled(26 * uiScale);
            dateYearFont.SetupContext(ctx);
            var yearExt = dateYearFont.GetTextExtents(yearLabel);
            double badgePadX = scaled(6 * uiScale);
            double badgePadY = scaled(2 * uiScale);
            double badgeW = yearExt.Width + badgePadX * 2;
            double badgeH = scaled(17 * uiScale);

            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.14);
            RoundRectangle(ctx, left, dateY - badgePadY, badgeW, badgeH, scaled(3 * uiScale));
            ctx.Fill();
            ctx.LineWidth = scaled(1);
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.34);
            RoundRectangle(ctx, left + scaled(0.5), dateY - badgePadY + scaled(0.5), badgeW - scaled(1), badgeH - scaled(1), scaled(3 * uiScale));
            ctx.Stroke();

            DrawText(ctx, dateYearFont, yearLabel, left + badgePadX, dateY);
            DrawText(ctx, dateDetailFont, dateDetail, left + badgeW + scaled(7 * uiScale), dateY + scaled(0.5 * uiScale));
        }

        string online = players.Count == 1 ? "1 ONLINE" : $"{players.Count} ONLINE";
        string slots = snapshot.MaxPlayers > 0 ? $" / {snapshot.MaxPlayers}" : "";
        string stat = online + slots;
        statFont.SetupContext(ctx);
        var ext = statFont.GetTextExtents(stat);
        DrawText(ctx, statFont, stat, x + width - scaled(pad) - ext.Width, top + scaled(4 * uiScale));

        double ruleY = y + scaled(60 * uiScale);
        ctx.SetSourceRGBA(1, 1, 1, 0.10);
        ctx.Rectangle(left, ruleY, width - scaled(pad * 2), scaled(1));
        ctx.Fill();

        if (showCapacityBar && snapshot.MaxPlayers > 0)
        {
            double barY = y + scaled(66 * uiScale);
            double barW = width - scaled(pad * 2);
            double ratio = Math.Clamp(players.Count / (double)Math.Max(1, snapshot.MaxPlayers), 0, 1);
            ctx.SetSourceRGBA(1, 1, 1, 0.09);
            RoundRectangle(ctx, left, barY, barW, scaled(3 * uiScale), scaled(1.5));
            ctx.Fill();
            if (ratio > 0.001)
            {
                ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.88);
                RoundRectangle(ctx, left, barY, barW * ratio, scaled(3 * uiScale), scaled(1.5));
                ctx.Fill();
            }
        }
    }

    private void DrawColumnHeaders(Context ctx, double x, double y, double width)
    {
        double startX = x + scaled(pad);
        for (int col = 0; col < columns; col++)
        {
            double cx = startX + col * (scaled(cellWidth) + scaled(gap));
            DrawText(ctx, mutedFont, "PLAYER", cx + scaled(showInitials ? 46 * uiScale : 10 * uiScale), y + scaled(3 * uiScale));
            if (showPing)
            {
                string ping = "PING";
                mutedFont.SetupContext(ctx);
                double tw = mutedFont.GetTextExtents(ping).Width;
                DrawText(ctx, mutedFont, ping, cx + scaled(cellWidth) - tw - scaled(10 * uiScale), y + scaled(3 * uiScale));
            }
        }
    }

    private void DrawRoster(Context ctx, double x, double bodyY)
    {
        double startX = x + scaled(pad);
        for (int index = 0; index < renderCount; index++)
        {
            int col = index / rows;
            int row = index % rows;
            double cx = startX + col * (scaled(cellWidth) + scaled(gap));
            double cy = bodyY + row * (scaled(rowHeight) + scaled(2 * uiScale));
            DrawPlayerRow(ctx, players[index], cx, cy, scaled(cellWidth), scaled(rowHeight), index);
        }
    }

    private void DrawPlayerRow(Context ctx, PublicPlayerListEntry entry, double x, double y, double width, double height, int index)
    {
        var roleColor = RoleColor(entry);

        ctx.SetSourceRGBA(1, 1, 1, index % 2 == 0 ? 0.055 : 0.035);
        RoundRectangle(ctx, x, y, width, height, scaled(4 * uiScale));
        ctx.Fill();

        ctx.SetSourceRGBA(roleColor[0], roleColor[1], roleColor[2], entry.Afk ? 0.50 : 0.82);
        RoundRectangle(ctx, x, y + scaled(6 * uiScale), scaled(3 * uiScale), height - scaled(12 * uiScale), scaled(1.5));
        ctx.Fill();

        double identityX = x + scaled(12 * uiScale);
        if (showInitials)
        {
            double size = scaled(compactRows ? 28 * uiScale : 32 * uiScale);
            double avatarX = x + scaled(11 * uiScale);
            double avatarY = y + (height - size) / 2;
            ctx.SetSourceRGBA(roleColor[0], roleColor[1], roleColor[2], 0.20);
            ctx.Arc(avatarX + size / 2, avatarY + size / 2, size / 2, 0, Math.PI * 2);
            ctx.Fill();
            ctx.LineWidth = scaled(1);
            ctx.SetSourceRGBA(roleColor[0], roleColor[1], roleColor[2], 0.55);
            ctx.Arc(avatarX + size / 2, avatarY + size / 2, size / 2 - scaled(0.5), 0, Math.PI * 2);
            ctx.Stroke();

            string initial = Initial(entry.PlayerName);
            nameFont.SetupContext(ctx);
            var te = nameFont.GetTextExtents(initial);
            var fe = nameFont.GetFontExtents();
            DrawText(ctx, nameFont, initial,
                avatarX + (size - te.Width) / 2,
                avatarY + (size - fe.Height) / 2);
            identityX = avatarX + size + scaled(10 * uiScale);
        }

        double top = y + scaled(compactRows ? 5 * uiScale : 7 * uiScale);
        string playerName = string.IsNullOrWhiteSpace(entry.PlayerName) ? "Player" : entry.PlayerName.Trim();
        DrawText(ctx, NameFont(entry), playerName, identityX, top);

        if (entry.Afk)
        {
            string afk = "AFK";
            tinyFont.SetupContext(ctx);
            double afkWidth = tinyFont.GetTextExtents(afk).Width + scaled(10 * uiScale);
            double afkX = x + width - scaled(showPing ? 102 * uiScale : 12 * uiScale) - afkWidth;
            ctx.SetSourceRGBA(0.84, 0.64, 0.26, 0.18);
            RoundRectangle(ctx, afkX, top - scaled(1), afkWidth, scaled(18 * uiScale), scaled(4));
            ctx.Fill();
            DrawText(ctx, adminFont, afk, afkX + scaled(5 * uiScale), top + scaled(1 * uiScale));
        }

        string meta = BuildMeta(entry);
        if (!string.IsNullOrWhiteSpace(meta))
        {
            CairoFont metaFont = RoleFont(entry);
            DrawText(ctx, metaFont, meta, identityX, y + scaled(compactRows ? 23 * uiScale : 29 * uiScale));
        }

        if (showPing)
        {
            double pingX = x + width - scaled(86 * uiScale);
            double pingY = y + scaled(compactRows ? 8 * uiScale : 12 * uiScale);
            if (showPingBars)
            {
                DrawPingBars(ctx, entry.PingMs, pingX, pingY);
                pingX += scaled(25 * uiScale);
            }

            string pingText = entry.PingMs < 0 ? "—" : $"{entry.PingMs}ms";
            CairoFont pingFont = PingFont(entry.PingMs);
            pingFont.SetupContext(ctx);
            var pe = pingFont.GetTextExtents(pingText);
            DrawText(ctx, pingFont, pingText, x + width - scaled(10 * uiScale) - pe.Width, pingY + scaled(2 * uiScale));
        }
    }

    private void DrawPingBars(Context ctx, int pingMs, double x, double y)
    {
        int active = PingBars(pingMs);
        var color = PingColor(pingMs);
        double barW = scaled(3 * uiScale);
        double gapW = scaled(2 * uiScale);
        for (int i = 0; i < 4; i++)
        {
            double h = scaled((5 + i * 3) * uiScale);
            double bx = x + i * (barW + gapW);
            double by = y + scaled(14 * uiScale) - h;
            if (i < active)
                ctx.SetSourceRGBA(color[0], color[1], color[2], 0.95);
            else
                ctx.SetSourceRGBA(1, 1, 1, 0.13);
            RoundRectangle(ctx, bx, by, barW, h, scaled(1));
            ctx.Fill();
        }
    }

    private void DrawFooter(Context ctx, double x, double y, double width, double height)
    {
        if (!showFooterHint && players.Count <= renderCount) return;

        string left = players.Count > renderCount
            ? $"Showing {renderCount} of {players.Count} players"
            : "Hold Tab to view roster";
        string right = "LIVE";
        double fy = y + height - scaled(20 * uiScale);
        DrawText(ctx, mutedFont, left, x + scaled(pad), fy);

        tinyFont.SetupContext(ctx);
        var ext = tinyFont.GetTextExtents(right);
        ctx.SetSourceRGBA(0.34, 0.78, 0.43, 0.85);
        ctx.Arc(x + width - scaled(pad) - ext.Width - scaled(10 * uiScale), fy + scaled(6 * uiScale), scaled(2.2), 0, Math.PI * 2);
        ctx.Fill();
        DrawText(ctx, mutedFont, right, x + width - scaled(pad) - ext.Width, fy);
    }

    private bool TryFormatInGameDate(out string yearLabel, out string dateDetail)
    {
        yearLabel = "";
        dateDetail = "";

        try
        {
            var calendar = capi.World?.Calendar;
            if (calendar == null) return false;

            int daysPerMonth = Math.Max(1, calendar.DaysPerMonth);
            int month = Math.Max(1, calendar.Month);
            int day = calendar.DayOfYear - (month - 1) * daysPerMonth + 1;
            day = Math.Clamp(day, 1, daysPerMonth);

            string monthName = Localize($"newfiesplayerlist:calendar-month-{month}", calendar.MonthName.ToString());
            if (string.IsNullOrWhiteSpace(monthName))
                monthName = $"{Localize("newfiesplayerlist:calendar-month", "Month")} {month}";

            string yearWord = Localize("newfiesplayerlist:calendar-year", "YEAR");
            string dayWord = Localize("newfiesplayerlist:calendar-day", "Day");
            yearLabel = $"{yearWord} {calendar.Year}";
            dateDetail = $"{monthName}  •  {dayWord} {day}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Localize(string key, string fallback)
    {
        string translated = Lang.Get(key);
        return string.IsNullOrWhiteSpace(translated) || string.Equals(translated, key, StringComparison.Ordinal)
            ? fallback
            : translated;
    }

    private string BuildMeta(PublicPlayerListEntry entry)
    {
        var parts = new List<string>(2);
        if (showRoles)
            parts.Add(string.IsNullOrWhiteSpace(entry.RoleName) ? "Player" : entry.RoleName.Trim());
        if (showSession && entry.SessionSeconds >= 0)
            parts.Add("Online " + FormatSession(entry.SessionSeconds));
        return string.Join("  •  ", parts);
    }

    private CairoFont NameFont(PublicPlayerListEntry entry)
    {
        string hex = NormalizeHex(entry.NameColorHex);
        if (hex.Length == 0) return nameFont;
        if (nameColorFonts.TryGetValue(hex, out var cached)) return cached;
        var color = ParseHex(hex, 1, 1, 1);
        cached = nameFont.Clone().WithColor(new[] { color[0], color[1], color[2], 1.0 });
        nameColorFonts[hex] = cached;
        return cached;
    }

    private CairoFont RoleFont(PublicPlayerListEntry entry)
    {
        string hex = NormalizeHex(entry.NameColorHex);
        if (hex.Length > 0)
        {
            if (detailColorFonts.TryGetValue(hex, out var cached)) return cached;
            var color = ParseHex(hex, 0.62, 0.66, 0.68);
            cached = detailFont.Clone().WithColor(new[] { color[0], color[1], color[2], 1.0 });
            detailColorFonts[hex] = cached;
            return cached;
        }

        string r = (entry.RoleName ?? "").ToLowerInvariant();
        if (r.Contains("owner") || r.Contains("admin")) return adminFont;
        if (r.Contains("moderator") || r.Contains("mod") || r.Contains("staff")) return staffFont;
        return normalRoleFont;
    }

    private static double[] RoleColor(PublicPlayerListEntry entry)
    {
        string hex = NormalizeHex(entry.NameColorHex);
        if (hex.Length > 0) return ParseHex(hex, 0.48, 0.72, 0.52);

        string r = (entry.RoleName ?? "").ToLowerInvariant();
        if (r.Contains("owner") || r.Contains("admin")) return new[] { 0.93, 0.76, 0.42 };
        if (r.Contains("moderator") || r.Contains("mod") || r.Contains("staff")) return new[] { 0.49, 0.72, 0.90 };
        return new[] { 0.48, 0.72, 0.52 };
    }

    private static string NormalizeHex(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6) return string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return string.Empty;
        }
        return "#" + text.ToUpperInvariant();
    }

    private CairoFont PingFont(int pingMs)
    {
        if (pingMs < 0 || pingMs > 300) return pingBadFont;
        if (pingMs <= 80) return pingGoodFont;
        if (pingMs <= 160) return pingFairFont;
        return pingPoorFont;
    }

    private static double[] PingColor(int pingMs)
    {
        if (pingMs < 0 || pingMs > 300) return new[] { 0.88, 0.40, 0.40 };
        if (pingMs <= 80) return new[] { 0.45, 0.84, 0.48 };
        if (pingMs <= 160) return new[] { 0.85, 0.80, 0.38 };
        return new[] { 0.91, 0.62, 0.34 };
    }

    private static int PingBars(int pingMs)
    {
        if (pingMs < 0) return 0;
        if (pingMs <= 80) return 4;
        if (pingMs <= 160) return 3;
        if (pingMs <= 300) return 2;
        return 1;
    }

    private static string Initial(string? name)
    {
        name = (name ?? "").Trim();
        return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();
    }

    private void DrawCentered(Context ctx, CairoFont font, string value, double x, double y, double width)
    {
        font.SetupContext(ctx);
        var ext = font.GetTextExtents(value ?? "");
        DrawText(ctx, font, value ?? "", x + Math.Max(0, (width - ext.Width) / 2), y);
    }

    private void DrawText(Context ctx, CairoFont font, string value, double x, double y)
    {
        if (string.IsNullOrEmpty(value)) return;
        font.SetupContext(ctx);
        text.DrawTextLine(ctx, font, value, x, y);
    }

    private static string FormatSession(long seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60) return "<1m";
        long minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        long hours = minutes / 60;
        minutes %= 60;
        if (hours < 24) return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        long days = hours / 24;
        hours %= 24;
        return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
    }

    private static double[] ParseHex(string? value, double fallbackR, double fallbackG, double fallbackB)
    {
        string s = (value ?? "").Trim().TrimStart('#');
        if (s.Length != 6) return new[] { fallbackR, fallbackG, fallbackB };
        try
        {
            int r = Convert.ToInt32(s.Substring(0, 2), 16);
            int g = Convert.ToInt32(s.Substring(2, 2), 16);
            int b = Convert.ToInt32(s.Substring(4, 2), 16);
            return new[] { r / 255d, g / 255d, b / 255d };
        }
        catch
        {
            return new[] { fallbackR, fallbackG, fallbackB };
        }
    }

    public override void Dispose()
    {
        // CairoFont in Vintage Story 1.22.7 is a lightweight style/config object.
        // Its Dispose() blindly calls Dispose() on an internal FontOptions field that
        // is only created after SetupContext(). Some role/ping fonts may never be
        // rendered before this roster is recomposed, leaving that field null and
        // causing a client crash during GuiComposer.Dispose(). Do not manually
        // dispose these font configs; the composer owns the actual GUI resources.
        base.Dispose();
    }
}
