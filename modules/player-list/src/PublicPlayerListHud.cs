using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace NewfiesPlayerList;

/// <summary>
/// Hold-Tab roster host. Rendering is delegated to one custom Cairo surface so the HUD
/// remains compact, polished and free from the crash-prone shaded/partial-blur path.
/// </summary>
public sealed class PublicPlayerListHud : HudElement
{
    private readonly PlayerListKeyHandler keyHandler;
    private PublicPlayerListResponse snapshot = new();
    private bool loading = true;
    private string statusMessage = "";
    private string lastCalendarDateKey = "";

    public PublicPlayerListHud(ICoreClientAPI capi, PlayerListKeyHandler keyHandler) : base(capi)
    {
        this.keyHandler = keyHandler;
    }

    public void EnsureComposed()
    {
        if (SingleComposer == null) Compose();
    }

    public override string ToggleKeyCombinationCode => null!;
    public override bool Focusable => false;
    public override bool PrefersUngrabbedMouse => false;
    public override double InputOrder => 1.0999;
    public override double DrawOrder => 0.8899;
    public override float ZSize => 200f;

    public override bool ShouldReceiveRenderEvents() => keyHandler.IsHeld;
    public override bool CaptureAllInputs() => false;
    public override bool CaptureRawMouse() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool TryClose() => false;
    public override void Toggle() { }
    public override void UnFocus() { }
    public override void Focus() { }
    public override bool Focused => false;
    protected override void OnFocusChanged(bool on) => focused = false;

    public void ShowLoading()
    {
        if (loading && string.IsNullOrEmpty(statusMessage) && SingleComposer != null) return;
        loading = true;
        statusMessage = "";
        Compose();
    }

    public void ShowStatus(string message)
    {
        message ??= "";
        if (!loading && string.Equals(statusMessage, message, StringComparison.Ordinal) && SingleComposer != null) return;
        loading = false;
        statusMessage = message;
        Compose();
    }

    public void UpdateSnapshot(PublicPlayerListResponse response)
    {
        response ??= new PublicPlayerListResponse();
        string calendarDateKey = CalendarDateKey();
        bool changed = loading ||
                       !string.IsNullOrEmpty(statusMessage) ||
                       !SnapshotsEqual(snapshot, response) ||
                       !string.Equals(lastCalendarDateKey, calendarDateKey, StringComparison.Ordinal);
        snapshot = response;
        loading = false;
        statusMessage = "";
        if (changed) Compose();
    }

    private void Compose()
    {
        ClearComposers();
        lastCalendarDateKey = CalendarDateKey();

        var elementBounds = ElementBounds.Fixed(0, 0, 10, 10);
        var roster = new PlayerRosterElement(capi, snapshot, loading, statusMessage, elementBounds);

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(ParseAlignment(snapshot.Alignment));
        dialogBounds.fixedX = snapshot.OffsetX;
        dialogBounds.fixedY = snapshot.OffsetY == 0 ? 22 : snapshot.OffsetY;

        var composer = capi.Gui.CreateCompo("newfies-player-list", dialogBounds)
            .AddStaticElement(roster)
            .Compose();

        SingleComposer = composer;
    }

    private string CalendarDateKey()
    {
        try
        {
            var calendar = capi.World?.Calendar;
            if (calendar == null) return "";

            int daysPerMonth = Math.Max(1, calendar.DaysPerMonth);
            int month = Math.Max(1, calendar.Month);
            int day = calendar.DayOfYear - (month - 1) * daysPerMonth + 1;
            day = Math.Clamp(day, 1, daysPerMonth);
            return $"{calendar.Year}:{month}:{day}";
        }
        catch
        {
            return "";
        }
    }

    private static EnumDialogArea ParseAlignment(string? value)
    {
        value = (value ?? "").Trim();
        if (value.Equals("LeftTop", StringComparison.OrdinalIgnoreCase)) return EnumDialogArea.LeftTop;
        if (value.Equals("RightTop", StringComparison.OrdinalIgnoreCase)) return EnumDialogArea.RightTop;
        return EnumDialogArea.CenterTop;
    }

    private static bool SnapshotsEqual(PublicPlayerListResponse a, PublicPlayerListResponse b)
    {
        if (!string.Equals(a.ServerName ?? "", b.ServerName ?? "", StringComparison.Ordinal) ||
            a.MaxPlayers != b.MaxPlayers ||
            Math.Abs(a.BackgroundOpacity - b.BackgroundOpacity) > 0.001f ||
            a.ShowRoles != b.ShowRoles ||
            a.ShowPing != b.ShowPing ||
            a.ShowSessionTime != b.ShowSessionTime ||
            a.StyleVersion != b.StyleVersion ||
            Math.Abs(a.LayoutScale - b.LayoutScale) > 0.001f ||
            a.RowsPerColumn != b.RowsPerColumn ||
            a.MaxColumns != b.MaxColumns ||
            !string.Equals(a.Alignment ?? "", b.Alignment ?? "", StringComparison.OrdinalIgnoreCase) ||
            a.OffsetX != b.OffsetX || a.OffsetY != b.OffsetY ||
            a.ShowPlayerInitials != b.ShowPlayerInitials ||
            a.ShowCapacityBar != b.ShowCapacityBar ||
            a.ShowColumnHeaders != b.ShowColumnHeaders ||
            a.CompactRows != b.CompactRows ||
            a.ShowPingBars != b.ShowPingBars ||
            a.ShowFooterHint != b.ShowFooterHint ||
            !string.Equals(a.AccentHex ?? "", b.AccentHex ?? "", StringComparison.OrdinalIgnoreCase))
            return false;

        var ap = a.Players ?? new List<PublicPlayerListEntry>();
        var bp = b.Players ?? new List<PublicPlayerListEntry>();
        if (ap.Count != bp.Count) return false;

        for (int i = 0; i < ap.Count; i++)
        {
            if (!string.Equals(ap[i].PlayerName ?? "", bp[i].PlayerName ?? "", StringComparison.Ordinal) ||
                !string.Equals(ap[i].RoleName ?? "", bp[i].RoleName ?? "", StringComparison.Ordinal) ||
                !string.Equals(ap[i].NameColorHex ?? "", bp[i].NameColorHex ?? "", StringComparison.OrdinalIgnoreCase) ||
                RoundedPing(ap[i].PingMs) != RoundedPing(bp[i].PingMs) ||
                ap[i].SessionSeconds / 60 != bp[i].SessionSeconds / 60 ||
                ap[i].Afk != bp[i].Afk)
                return false;
        }
        return true;
    }

    private static int RoundedPing(int pingMs)
    {
        if (pingMs < 0) return -1;
        return (int)Math.Round(pingMs / 5d) * 5;
    }
}
