using System;

namespace BlockLogger.Admin;

public sealed class AdminInventorySnapshotSlot
{
	public string Inventory { get; set; } = "";

	public int Slot { get; set; }

	public string StackBase64 { get; set; } = "";

	public byte[] StackBytes { get; set; } = Array.Empty<byte>();

	public string ItemCode { get; set; } = "";

	public int StackSize { get; set; }
}
