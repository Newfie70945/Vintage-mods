using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string Operation { get; set; } = "get";

	[ProtoMember(3)]
	public int DurationSeconds { get; set; } = 10;

	[ProtoMember(4)]
	public bool BoolValue { get; set; }

	[ProtoMember(5)]
	public bool Compact { get; set; }
}
