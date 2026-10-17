using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class InvestigatorStatusRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }
}
