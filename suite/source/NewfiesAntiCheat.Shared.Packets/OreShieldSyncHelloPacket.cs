using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class OreShieldSyncHelloPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; } = 3;

	[ProtoMember(2)]
	public string ClientVersion { get; set; } = "";

	[ProtoMember(3)]
	public string Capability { get; set; } = "";

	[ProtoMember(4)]
	public string BuildMarker { get; set; } = "";
}
