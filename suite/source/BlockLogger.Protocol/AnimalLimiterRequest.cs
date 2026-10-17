using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AnimalLimiterRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string Operation { get; set; } = "status";

	[ProtoMember(3)]
	public string Pattern { get; set; } = "";

	[ProtoMember(4)]
	public int Limit { get; set; } = 20;

	[ProtoMember(5)]
	public int RadiusChunks { get; set; }

	[ProtoMember(6)]
	public int IntValue { get; set; }

	[ProtoMember(7)]
	public bool BoolValue { get; set; }

	[ProtoMember(8)]
	public string SettingKey { get; set; } = "";
}
