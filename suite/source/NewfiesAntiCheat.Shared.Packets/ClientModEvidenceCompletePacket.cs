using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientModEvidenceCompletePacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string RequestId { get; set; } = "";

	[ProtoMember(3)]
	public bool Success { get; set; }

	[ProtoMember(4)]
	public string Message { get; set; } = "";

	[ProtoMember(5)]
	public long TotalBytes { get; set; }

	[ProtoMember(6)]
	public string Sha256 { get; set; } = "";
}
