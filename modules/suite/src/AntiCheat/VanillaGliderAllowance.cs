using System;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace BlockLogger.AntiCheat;

// A compatibility envelope, never a trusted teleport/noclip exemption. The native
// PModulePlayerInAir accelerates gliders beyond ordinary walking/airborne limits.
internal sealed class VanillaGliderAllowance
{
    internal const double HorizontalCeiling = 120.0;
    private long until, entityId;
    private int dimension;

    internal bool Observe(bool equipped, bool gliding, long now, long id, int dim)
    {
        if (id != entityId || dim != dimension) until = 0;
        entityId = id; dimension = dim;
        if (!equipped) { until = 0; return false; }
        if (gliding) until = now + 2000;
        return until > now;
    }

    internal static bool IsGliderSlot(ItemSlot? slot) => slot is ItemSlotBackpack &&
        slot.Itemstack?.Collectible is ItemGlider item && item.Code?.Domain == "game" && item.Code.Path == "glider";

    internal static bool Equipped(IPlayer player)
    {
        try
        {
            var inventory = player.InventoryManager?.GetOwnInventory("backpack");
            if (inventory == null) return false;
            // Only equipped backpack slots count, never a glider carried in bag contents.
            for (int i = 0; i < Math.Min(inventory.Count, 16); i++)
                if (IsGliderSlot(inventory[i])) return true;
        }
        catch { /* Missing/custom inventories receive no blanket movement exemption. */ }
        return false;
    }
}
