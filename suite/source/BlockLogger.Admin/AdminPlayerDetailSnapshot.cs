using System.Collections.Generic;
using BlockLogger.Models;

namespace BlockLogger.Admin;

public sealed class AdminPlayerDetailSnapshot
{
	public AdminPlayerSummary Player { get; set; } = new AdminPlayerSummary();

	public List<AdminWarningRecord> Warnings { get; set; } = new List<AdminWarningRecord>();

	public List<AdminNoteRecord> Notes { get; set; } = new List<AdminNoteRecord>();

	public List<AdminAuditRecord> History { get; set; } = new List<AdminAuditRecord>();

	public List<AdminInventorySlotView> Inventory { get; set; } = new List<AdminInventorySlotView>();

	public AdminInventoryViewStatus InventoryStatus { get; set; } = new AdminInventoryViewStatus();

	public List<AdminInventorySnapshotRecord> InventorySnapshots { get; set; } = new List<AdminInventorySnapshotRecord>();

	public List<LogEventRecord> BlockHistory { get; set; } = new List<LogEventRecord>();
}
