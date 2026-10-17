using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class InvestigatorQueryResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string View { get; set; } = "blocks";

	[ProtoMember(4)]
	public string Header { get; set; } = "";

	[ProtoMember(5)]
	public string Error { get; set; } = "";

	[ProtoMember(6)]
	public List<string> Lines { get; set; } = new List<string>();
}
