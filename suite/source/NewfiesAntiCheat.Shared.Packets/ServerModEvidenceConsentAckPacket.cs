using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ServerModEvidenceConsentAckPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string RequestId { get; set; } = "";

	[ProtoMember(3)]
	public bool Authorized { get; set; }

	[ProtoMember(4)]
	public string Message { get; set; } = "";
}
