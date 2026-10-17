using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class OreShieldChunkSyncRequestPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; } = 3;

	[ProtoMember(2)]
	public int ChunkX { get; set; }

	[ProtoMember(3)]
	public int ChunkY { get; set; }

	[ProtoMember(4)]
	public int ChunkZ { get; set; }

	[ProtoMember(5)]
	public long KnownRevision { get; set; }
}
