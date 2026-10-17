using System.Text.Json.Serialization;

namespace BlockLogger.Admin;

public sealed class AdminInventorySlotView
{
	public string Inventory { get; set; } = "";

	public string Section { get; set; } = "Other";

	public int Slot { get; set; }

	public bool Empty { get; set; }

	public string Code { get; set; } = "";

	public string Name { get; set; } = "";

	public string ItemClass { get; set; } = "";

	public int StackSize { get; set; }

	public int MaxStackSize { get; set; }

	public int MaxSlotStackSize { get; set; }

	public int Durability { get; set; }

	public int MaxDurability { get; set; }

	public float Temperature { get; set; }

	public string StorageType { get; set; } = "";

	public string Fingerprint { get; set; } = "empty";

	[JsonIgnore]
	public string SerializedStack { get; set; } = "";
}
