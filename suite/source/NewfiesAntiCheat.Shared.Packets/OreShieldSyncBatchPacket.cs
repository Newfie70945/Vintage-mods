using System;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class OreShieldSyncBatchPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; } = 3;

	[ProtoMember(2)]
	public long Sequence { get; set; }

	[ProtoMember(3)]
	public int ChunkX { get; set; }

	[ProtoMember(4)]
	public int ChunkY { get; set; }

	[ProtoMember(5)]
	public int ChunkZ { get; set; }

	[ProtoMember(6)]
	public long Revision { get; set; }

	[ProtoMember(7)]
	public int[] LocalIndices { get; set; } = Array.Empty<int>();

	[ProtoMember(8)]
	public int[] BlockIds { get; set; } = Array.Empty<int>();

	[ProtoMember(9)]
	public int ApplyMode { get; set; }
}
