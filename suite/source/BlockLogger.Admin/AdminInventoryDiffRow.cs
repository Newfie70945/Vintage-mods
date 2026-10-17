namespace BlockLogger.Admin;

public sealed class AdminInventoryDiffRow
{
	public string Inventory { get; set; } = "";

	public int Slot { get; set; }

	public string SnapshotItem { get; set; } = "";

	public string CurrentItem { get; set; } = "";

	public string Change { get; set; } = "";
}
