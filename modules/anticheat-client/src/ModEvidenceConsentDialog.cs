using System;
using Vintagestory.API.Client;

namespace NewfiesAntiCheatClient;

/// <summary>
/// Per-request authorization dialog for a single suspicious mod-package transfer.
/// Closing the dialog or pressing Escape is treated as an explicit decline.
/// </summary>
internal sealed class ModEvidenceConsentDialog : GuiDialog
{
    private readonly string text;
    private readonly Action<bool> onDecision;
    private bool resolved;
    private bool disposed;

    public ModEvidenceConsentDialog(ICoreClientAPI capi, string text, Action<bool> onDecision) : base(capi)
    {
        this.text = text ?? "";
        this.onDecision = onDecision;
        Compose();
    }

    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.8;
    public override bool PrefersUngrabbedMouse => true;
    public override bool DisableMouseGrab => true;

    public override bool TryOpen()
    {
        if (disposed) return false;
        Compose();
        return base.TryOpen();
    }

    public override bool TryClose()
    {
        if (!resolved) Resolve(false);
        return base.TryClose();
    }

    private void Compose()
    {
        ElementBounds content = ElementBounds.Fixed(0, 0, 700, 520);
        ElementBounds background = ElementBounds.Fill.WithFixedPadding(14);
        background.BothSizing = ElementSizing.FitToChildren;
        background.WithChildren(content);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        ClearComposers();
        SingleComposer = capi.Gui
            .CreateCompo("newfies-anticheat-mod-transfer-authorization", dialogBounds)
            // Do not use AddDialogTitleBar here. Its binary signature changed across
            // Vintage Story 1.22 builds, which can make a client compiled against a
            // newer API throw MissingMethodException on an older 1.22 runtime.
            // A normal static header + explicit close button is API-stable and keeps
            // closing the dialog equivalent to declining the transfer request.
            .AddShadedDialogBG(background, false)
            .BeginChildElements(background)
                .AddStaticText("Server Mod Transfer Request", CairoFont.WhiteMediumText(), ElementBounds.Fixed(12, 12, 610, 32))
                .AddSmallButton("X", OnDecline, ElementBounds.Fixed(640, 8, 48, 32), EnumButtonStyle.Normal)
                .AddStaticText(text, CairoFont.WhiteSmallText(), ElementBounds.Fixed(12, 54, 676, 378))
                .AddSmallButton("Decline", OnDecline, ElementBounds.Fixed(12, 452, 310, 42), EnumButtonStyle.Normal)
                .AddSmallButton("Agree & Transfer", OnAgree, ElementBounds.Fixed(366, 452, 322, 42), EnumButtonStyle.Normal)
            .EndChildElements()
            .Compose();
    }

    private bool OnAgree()
    {
        Resolve(true);
        base.TryClose();
        return true;
    }

    private bool OnDecline()
    {
        Resolve(false);
        base.TryClose();
        return true;
    }

    private void Resolve(bool accepted)
    {
        if (resolved) return;
        resolved = true;
        try { onDecision(accepted); } catch { }
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        Resolve(false);
        ClearComposers();
        // Closing leaves the dialog in LoadedGuis. Remove this one-shot dialog
        // after the current GUI event has finished enumerating that collection.
        capi.Event.EnqueueMainThreadTask(Dispose, "newfies-evidence-dialog-dispose");
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearComposers();
        capi.Gui.LoadedGuis.Remove(this);
        base.Dispose();
    }
}
