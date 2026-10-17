using ProtoBuf;

namespace BlockLogger.PlayerList;

[ProtoContract]
public sealed class PublicPlayerListClientHello
{
	[ProtoMember(1)]
	public string ClientVersion { get; set; } = "";

	[ProtoMember(2)]
	public int ProtocolVersion { get; set; } = 1;

	[ProtoMember(3)]
	public string Capabilities { get; set; } = "";
}
