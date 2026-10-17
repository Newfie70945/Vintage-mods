using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class BlockLoggerSettingsResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Message { get; set; } = "";

	[ProtoMember(4)]
	public bool Enabled { get; set; }

	[ProtoMember(5)]
	public bool LogBlockBreak { get; set; }

	[ProtoMember(6)]
	public bool LogBlockPlace { get; set; }

	[ProtoMember(7)]
	public bool LogBlockUse { get; set; }

	[ProtoMember(8)]
	public bool LogContainerAccess { get; set; }

	[ProtoMember(9)]
	public bool LogContainerItemChanges { get; set; }

	[ProtoMember(10)]
	public bool LogEntityInteract { get; set; }

	[ProtoMember(11)]
	public bool LogEntitySpawn { get; set; }

	[ProtoMember(12)]
	public bool LogEntityDeath { get; set; }

	[ProtoMember(13)]
	public bool LogEntityDespawn { get; set; }

	[ProtoMember(14)]
	public bool AggregateEntitySpawnDespawn { get; set; }

	[ProtoMember(15)]
	public int RetentionDays { get; set; }

	[ProtoMember(16)]
	public int FlushIntervalMs { get; set; }

	[ProtoMember(17)]
	public int MaxQueuedEvents { get; set; }

	[ProtoMember(18)]
	public int DefaultRadius { get; set; }

	[ProtoMember(19)]
	public int EntityRadius { get; set; }

	[ProtoMember(20)]
	public int MaxRadius { get; set; }

	[ProtoMember(21)]
	public int DefaultDisplayCount { get; set; }

	[ProtoMember(22)]
	public int DefaultEntityDisplayCount { get; set; }

	[ProtoMember(23)]
	public int MaxDisplayCount { get; set; }

	[ProtoMember(24)]
	public int EmergencyOverflowCapacity { get; set; }
}
