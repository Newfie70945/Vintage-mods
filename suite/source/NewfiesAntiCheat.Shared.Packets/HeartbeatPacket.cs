using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class HeartbeatPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public long ClientTimestampUtc { get; set; }

	[ProtoMember(3)]
	public string ClientVersion { get; set; } = "";

	[ProtoMember(4)]
	public int RuntimeGeneration { get; set; }

	[ProtoMember(5)]
	public int LoadedAssemblyCount { get; set; }

	[ProtoMember(6)]
	public int DynamicAssemblyCount { get; set; }

	[ProtoMember(7)]
	public string RuntimeDigest { get; set; } = "";

	[ProtoMember(8)]
	public string SelfIntegrityDigest { get; set; } = "";

	[ProtoMember(9)]
	public string HarmonyPatchDigest { get; set; } = "";

	[ProtoMember(10)]
	public int HarmonyPatchCount { get; set; }

	[ProtoMember(11)]
	public int SuspiciousHarmonyOwnerCount { get; set; }

	[ProtoMember(12)]
	public long Sequence { get; set; }

	[ProtoMember(13)]
	public string ChallengeId { get; set; } = "";

	[ProtoMember(14)]
	public string BindingHash { get; set; } = "";

	[ProtoMember(15)]
	public int BindingVersion { get; set; }

	[ProtoMember(16)]
	public string SelfAttestationDigest { get; set; } = "";

	[ProtoMember(17)]
	public int SelfAttestationProbeCount { get; set; }
}
