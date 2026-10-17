using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ServerAckPacket
{
	[ProtoMember(1)]
	public string ChallengeId { get; set; } = "";

	[ProtoMember(2)]
	public bool Accepted { get; set; }

	[ProtoMember(3)]
	public string State { get; set; } = "";

	[ProtoMember(4)]
	public long NextScanUtc { get; set; }
}
