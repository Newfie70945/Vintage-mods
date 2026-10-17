using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientModEvidenceConsentPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string RequestId { get; set; } = "";

	[ProtoMember(3)]
	public bool Accepted { get; set; }

	[ProtoMember(4)]
	public long DecisionUtc { get; set; }
}
