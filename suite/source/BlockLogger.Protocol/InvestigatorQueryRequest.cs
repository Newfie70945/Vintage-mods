using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class InvestigatorQueryRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public string View { get; set; } = "blocks";

	[ProtoMember(3)]
	public bool UseSelection { get; set; } = true;

	[ProtoMember(4)]
	public int Radius { get; set; } = 5;

	[ProtoMember(5)]
	public int Limit { get; set; } = 20;

	[ProtoMember(6)]
	public string Search { get; set; } = "";

	[ProtoMember(7)]
	public string Action { get; set; } = "";
}
