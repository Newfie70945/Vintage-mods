using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

internal sealed class ReadonlyAdminInventoryGrid : GuiElementItemSlotGrid
{
	private readonly Action<int> onSlotSelected;

	public ReadonlyAdminInventoryGrid(ICoreClientAPI capi, IInventory inventory, int columns, ElementBounds bounds, Action<int> onSlotSelected)
		: base(capi, inventory, (object _) =>
		{
		}, columns, null, bounds)
	{
		this.onSlotSelected = onSlotSelected;
		KeyboardControlEnabled = false;
	}

	public override void SlotClick(ICoreClientAPI api, int slotId, EnumMouseButton mouseButton, bool shiftPressed, bool ctrlPressed, bool altPressed)
	{
		HighlightSlot(slotId);
		onSlotSelected?.Invoke(slotId);
	}
}
