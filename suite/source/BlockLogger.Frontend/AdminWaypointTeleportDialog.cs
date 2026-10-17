using System;
using Vintagestory.API.Client;

namespace BlockLogger.Frontend;

internal sealed class AdminWaypointTeleportDialog : GuiDialogGeneric
{
	private readonly Action onTeleport;

	private readonly int worldX;

	private readonly int worldZ;

	private bool disposed;

	public override bool PrefersUngrabbedMouse => true;

	public override bool DisableMouseGrab => true;

	public override EnumDialogType DialogType => EnumDialogType.Dialog;

	public override double DrawOrder => 0.215;

	public AdminWaypointTeleportDialog(ICoreClientAPI capi, int worldX, int worldZ, Action onTeleport)
		: base("", capi)
	{
		this.worldX = worldX;
		this.worldZ = worldZ;
		this.onTeleport = onTeleport;
	}

	public override bool CaptureAllInputs()
	{
		return false;
	}

	public override bool TryOpen()
	{
		if (disposed)
		{
			return false;
		}
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
		if (!disposed)
		{
			disposed = true;
			ClearComposers();
			capi.Gui.LoadedGuis.Remove(this);
			base.Dispose();
		}
	}

	private void ComposeDialog()
	{
		ElementBounds elementBounds = ElementBounds.Fixed(0.0, 0.0, 184.0, 72.0);
		ElementBounds elementBounds2 = ElementBounds.Fill.WithFixedPadding(8.0);
		elementBounds2.BothSizing = ElementSizing.FitToChildren;
		elementBounds2.WithChildren(elementBounds);
		ElementBounds bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle).WithFixedAlignmentOffset(320.0, -42.0);
		ClearComposers();
		SingleComposer = capi.Gui.CreateCompo("newfies-waypoint-teleport-action", bounds).AddShadedDialogBG(elementBounds2, withTitleBar: false).BeginChildElements(elementBounds2)
			.AddStaticText(SuiteLocalization.Get("gui.adminwaypointaction"), CairoFont.WhiteSmallText(), ElementBounds.Fixed(8.0, 4.0, 168.0, 20.0))
			.AddSmallButton(SuiteLocalization.Get("gui.teleporthere"), OnTeleport, ElementBounds.Fixed(8.0, 30.0, 168.0, 30.0), EnumButtonStyle.Normal, "waypoint-teleport-here")
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
