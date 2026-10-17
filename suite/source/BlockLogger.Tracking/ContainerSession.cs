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

	public required WeakReference<IInventory> InventoryRef { get; init; }

	public required Dictionary<int, TrackedSlotState> SlotStates { get; init; }

	public DateTime ExpiresAtUtc { get; set; }

	public bool Detached { get; set; }

	public Action<int>? SlotModifiedHandler { get; set; }

	public long? SnapshotCallbackId { get; set; }

	public bool TryGetInventory(out IInventory inventory)
	{
		if (InventoryRef.TryGetTarget(out IInventory target) && target != null)
		{
			inventory = target;
			return true;
		}
		inventory = null;
		return false;
	}

	public void SetActivePlayer(string uid, string name)
	{
		PlayerUid = uid;
		PlayerName = name;
	}
}
