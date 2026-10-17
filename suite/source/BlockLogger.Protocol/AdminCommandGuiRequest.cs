using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AdminCommandGuiRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string Operation { get; set; } = "player-info";

	[ProtoMember(3)]
	public string Target { get; set; } = "";

	[ProtoMember(4)]
	public string Value { get; set; } = "";

	[ProtoMember(5)]
	public string Secondary { get; set; } = "";

	[ProtoMember(6)]
	public string Reason { get; set; } = "";

	[ProtoMember(7)]
	public int IntValue { get; set; }

	[ProtoMember(8)]
	public int IntValue2 { get; set; }

	[ProtoMember(9)]
	public long LongValue { get; set; }

	[ProtoMember(10)]
	public bool Confirmed { get; set; }

	[ProtoMember(11)]
	public string ExpectedFingerprint { get; set; } = "";
}
