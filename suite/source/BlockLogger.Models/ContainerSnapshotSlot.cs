namespace BlockLogger.Models;

public sealed class ContainerSnapshotSlot
{
	public int SlotId { get; set; }

	public string ItemCode { get; set; } = string.Empty;

	public int StackSize { get; set; }
}
