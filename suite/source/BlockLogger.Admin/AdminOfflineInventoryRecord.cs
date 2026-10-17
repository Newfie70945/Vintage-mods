using System.Collections.Generic;

namespace BlockLogger.Admin;

public sealed class AdminOfflineInventoryRecord
{
	public long CapturedUnixMilliseconds { get; set; }

	public string Source { get; set; } = "disconnect";

	public List<AdminInventorySlotView> Slots { get; set; } = new List<AdminInventorySlotView>();
}
