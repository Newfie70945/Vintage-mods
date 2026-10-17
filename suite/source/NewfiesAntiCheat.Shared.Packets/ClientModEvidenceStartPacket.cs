using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientModEvidenceStartPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string RequestId { get; set; } = "";

	[ProtoMember(3)]
	public string ModId { get; set; } = "";

	[ProtoMember(4)]
	public string OriginalFileName { get; set; } = "";

	[ProtoMember(5)]
	public long TotalBytes { get; set; }

	[ProtoMember(6)]
	public string Sha256 { get; set; } = "";

	[ProtoMember(7)]
	public bool GeneratedArchive { get; set; }

	[ProtoMember(8)]
	public int ChunkSize { get; set; }

	[ProtoMember(9)]
	public int TotalChunks { get; set; }
}
