using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class InvestigatorStatusResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Error { get; set; } = "";

	[ProtoMember(4)]
	public List<string> Lines { get; set; } = new List<string>();
}
