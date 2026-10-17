using System;
using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class AdminState
{
	public int SchemaVersion { get; set; } = 3;

	public Dictionary<string, PlayerAdminRecord> Players { get; set; } = new Dictionary<string, PlayerAdminRecord>(StringComparer.OrdinalIgnoreCase);

	public List<AdminAuditRecord> Audit { get; set; } = new List<AdminAuditRecord>();

	public bool MaintenanceMode { get; set; }

	public string MaintenanceReason { get; set; } = "Maintenance";

	public long MaintenanceUntilUnixMilliseconds { get; set; }

	public Dictionary<string, List<AdminInventorySnapshotRecord>> InventorySnapshots { get; set; } = new Dictionary<string, List<AdminInventorySnapshotRecord>>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, AdminOfflineInventoryRecord> OfflineInventories { get; set; } = new Dictionary<string, AdminOfflineInventoryRecord>(StringComparer.OrdinalIgnoreCase);
}
