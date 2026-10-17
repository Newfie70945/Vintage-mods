using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class SelfAttestationProbeDto
{
	[ProtoMember(1)]
	public string ProbeId { get; set; } = "";

	[ProtoMember(2)]
	public int Kind { get; set; }

	[ProtoMember(3)]
	public int MetadataToken { get; set; }
}
