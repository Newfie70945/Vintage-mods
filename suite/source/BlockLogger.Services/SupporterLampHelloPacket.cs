using ProtoBuf;

namespace BlockLogger.Services;

[ProtoContract]
public sealed class SupporterLampHelloPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; } = 1;
}
