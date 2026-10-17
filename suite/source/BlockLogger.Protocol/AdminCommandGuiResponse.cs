using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class AdminCommandGuiResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Operation { get; set; } = "";

	[ProtoMember(4)]
	public string Message { get; set; } = "";

	[ProtoMember(5)]
	public List<string> Lines { get; set; } = new List<string>();
}
