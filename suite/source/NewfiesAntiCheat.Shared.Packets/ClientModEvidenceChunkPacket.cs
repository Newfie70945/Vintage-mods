using System;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientModEvidenceChunkPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string RequestId { get; set; } = "";

	[ProtoMember(3)]
	public int ChunkIndex { get; set; }

	[ProtoMember(4)]
	public byte[] Data { get; set; } = Array.Empty<byte>();
}
