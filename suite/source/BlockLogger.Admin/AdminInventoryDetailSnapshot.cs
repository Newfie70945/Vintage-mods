using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class AdminInventoryDetailSnapshot
{
	public AdminPlayerSummary Player { get; set; } = new AdminPlayerSummary();

	public List<AdminInventorySlotView> Inventory { get; set; } = new List<AdminInventorySlotView>();

	public AdminInventoryViewStatus InventoryStatus { get; set; } = new AdminInventoryViewStatus();

	public List<AdminInventorySnapshotRecord> InventorySnapshots { get; set; } = new List<AdminInventorySnapshotRecord>();

	public List<AdminAuditRecord> History { get; set; } = new List<AdminAuditRecord>();
}
