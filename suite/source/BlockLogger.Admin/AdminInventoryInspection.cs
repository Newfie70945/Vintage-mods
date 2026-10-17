using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class AdminInventoryInspection
{
	public List<AdminInventorySlotView> Slots { get; set; } = new List<AdminInventorySlotView>();

	public AdminInventoryViewStatus Status { get; set; } = new AdminInventoryViewStatus();
}
