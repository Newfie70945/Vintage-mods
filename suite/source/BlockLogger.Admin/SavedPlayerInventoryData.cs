using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Admin;

[ProtoContract]
public sealed class SavedPlayerInventoryData
{
	public const int MaxSlots = 8192;

	public const int MaxBytes = 16777216;

	[ProtoMember(2)]
	public Dictionary<string, byte[]>? Inventories { get; set; }

	[ProtoIgnore]
	public long ReadUnixMilliseconds { get; set; }

	[ProtoIgnore]
	public string? Error { get; set; }
}
