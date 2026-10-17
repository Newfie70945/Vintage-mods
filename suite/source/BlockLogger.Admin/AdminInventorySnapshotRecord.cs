using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace BlockLogger.Admin;

public sealed class AdminInventorySnapshotRecord
{
	public long Id { get; set; }

	public long UnixMilliseconds { get; set; }

	public string PlayerUid { get; set; } = "";

	public string PlayerName { get; set; } = "";

	public string CreatedBy { get; set; } = "";

	public string Reason { get; set; } = "";

	public int OccupiedSlots { get; set; }

	public List<AdminInventorySnapshotSlot> Slots { get; set; } = new List<AdminInventorySnapshotSlot>();

	[Newtonsoft.Json.JsonIgnore]
	[System.Text.Json.Serialization.JsonIgnore]
	public bool SlotsPayloadLoaded { get; set; } = true;
}
