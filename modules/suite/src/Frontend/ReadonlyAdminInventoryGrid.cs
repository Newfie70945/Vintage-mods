using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockLogger.Frontend;

/// <summary>
/// Vintage Story slot grid used by the Shift+L remote inventory viewer.
/// It deliberately suppresses normal inventory movement; a click only selects
/// the corresponding remote slot for the Suite's guarded admin editor.
/// </summary>
internal sealed class ReadonlyAdminInventoryGrid : GuiElementItemSlotGrid
{
    private readonly Action<int> onSlotSelected;

    public ReadonlyAdminInventoryGrid(
        ICoreClientAPI capi,
        IInventory inventory,
        int columns,
        ElementBounds bounds,
        Action<int> onSlotSelected)
        : base(capi, inventory, _ => { }, columns, null, bounds)
    {
        this.onSlotSelected = onSlotSelected;
        KeyboardControlEnabled = false;
    }

    public override void SlotClick(
        ICoreClientAPI api,
        int slotId,
        EnumMouseButton mouseButton,
        bool shiftPressed,
        bool ctrlPressed,
        bool altPressed)
    {
        HighlightSlot(slotId);
        onSlotSelected?.Invoke(slotId);
    }
}
