using System.Collections.Generic;

namespace BlockLogger.Services;

public sealed class RecoveryOperation
{
	public string Kind { get; set; } = "block";

	public RecoveryCell Cell { get; set; } = new RecoveryCell("0", 0, 0, 0);

	public string Before { get; set; } = "";

	public string After { get; set; } = "";

	public List<RecoverySlot>? InventoryBefore { get; set; }

	public List<RecoverySlot>? InventoryAfter { get; set; }

	public int SlotCount { get; set; }

	public long OriginalEntityId { get; set; }

	public string EntityCode { get; set; } = "";

	public string ReceiptKey { get; set; } = "";

	public string Fingerprint { get; set; } = "";

	public List<long> SourceIds { get; set; } = new List<long>();
}
