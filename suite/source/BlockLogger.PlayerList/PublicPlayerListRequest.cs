using ProtoBuf;

namespace BlockLogger.PlayerList;

[ProtoContract]
public sealed class PublicPlayerListRequest
{
	[ProtoMember(1)]
	public long RequestId { get; set; }
}
