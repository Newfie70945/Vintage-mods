using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace BlockLogger.Tracking;

public sealed class ContainerSession
{
    public required string SessionId { get; init; }
    public string PlayerUid { get; private set; } = string.Empty;
    public string PlayerName { get; private set; } = string.Empty;
    public required string InventoryId { get; init; }
    public required string BlockCode { get; init; }
    public required BlockPos Position { get; init; }

    // Do not keep a strong reference to the live inventory. A container inventory can lead
    // back to its block entity/chunk/world object graph, so a missed session cleanup must not
    // be able to keep that graph alive. The inventory's own SlotModified event may reference
    // our handler, but that direction does not root the inventory from this session table.
    public required WeakReference<IInventory> InventoryRef { get; init; }

    public required Dictionary<int, TrackedSlotState> SlotStates { get; init; }

    // Only explicit player interaction extends a session. Automated hopper/machine/inventory
    // activity must never keep a logging session alive indefinitely.
    public DateTime ExpiresAtUtc { get; set; }

    public bool Detached { get; set; }
    public Action<int>? SlotModifiedHandler { get; set; }
    public long? SnapshotCallbackId { get; set; }

    public bool TryGetInventory(out IInventory inventory)
    {
        if (InventoryRef.TryGetTarget(out var target) && target != null)
        {
            inventory = target;
            return true;
        }

        inventory = null!;
        return false;
    }

    public void SetActivePlayer(string uid, string name)
    {
        PlayerUid = uid;
        PlayerName = name;
    }
}
