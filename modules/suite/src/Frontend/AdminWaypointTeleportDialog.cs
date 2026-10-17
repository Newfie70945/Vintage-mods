using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

/// <summary>
/// Small companion panel displayed beside Vintage Story's native Add/Edit Waypoint dialog.
/// It deliberately does not replace or modify the vanilla waypoint composer; the native dialog
/// keeps keyboard focus and all normal waypoint controls while Server Essentials adds one
/// server-authorized Teleport Here action beside it.
/// </summary>
internal sealed class AdminWaypointTeleportDialog : GuiDialogGeneric
{
    private readonly Action onTeleport;
    private readonly int worldX;
    private readonly int worldZ;
    private bool disposed;

    public AdminWaypointTeleportDialog(ICoreClientAPI capi, int worldX, int worldZ, Action onTeleport)
        : base("", capi)
    {
        this.worldX = worldX;
        this.worldZ = worldZ;
        this.onTeleport = onTeleport;
    }

    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;
    public override EnumDialogType DialogType => EnumDialogType.Dialog;

    // Vanilla's waypoint dialogs currently render around 0.2. Stay just above them so the
    // companion button remains clickable without covering the waypoint editor itself.
    public override double DrawOrder => 0.215;

    public override bool CaptureAllInputs() => false;

    public override bool TryOpen()
    {
        if (disposed) return false;
        ComposeDialog();
        return base.TryOpen();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        ClearComposers();
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearComposers();
        // A new companion is created for each native waypoint editor. The
        // engine's base disposer leaves it registered unless we remove it.
        capi.Gui.LoadedGuis.Remove(this);
        base.Dispose();
    }

    private void ComposeDialog()
    {
        ElementBounds content = ElementBounds.Fixed(0, 0, 184, 72);
        ElementBounds background = ElementBounds.Fill.WithFixedPadding(8);
        background.BothSizing = ElementSizing.FitToChildren;
        background.WithChildren(content);

        // Place the panel to the right of the centered vanilla waypoint editor. Keeping this
        // separate from the native composer avoids version-dependent GuiDialogAddWayPoint fields.
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle)
            .WithFixedAlignmentOffset(320, -42);

        ClearComposers();
        SingleComposer = capi.Gui
            .CreateCompo("newfies-waypoint-teleport-action", dialogBounds)
            .AddShadedDialogBG(background, false)
            .BeginChildElements(background)
                .AddStaticText(SuiteLocalization.Get("gui.adminwaypointaction"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(8, 4, 168, 20))
                .AddSmallButton(SuiteLocalization.Get("gui.teleporthere"), OnTeleport, ElementBounds.Fixed(8, 30, 168, 30), EnumButtonStyle.Normal, key: "waypoint-teleport-here")
            .EndChildElements()
            .Compose();
    }

    private bool OnTeleport()
    {
        capi.ShowChatMessage(SuiteLocalization.Get("gui.waypointselected", worldX.ToString("N0"), worldZ.ToString("N0")));
        onTeleport();
        return true;
    }
}
