using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class SelfAttestationResultDto
{
	[ProtoMember(1)]
	public string ProbeId { get; set; } = "";

	[ProtoMember(2)]
	public int Kind { get; set; }

	[ProtoMember(3)]
	public int MetadataToken { get; set; }

	[ProtoMember(4)]
	public string Digest { get; set; } = "";

	[ProtoMember(5)]
	public bool Success { get; set; }

	[ProtoMember(6)]
	public string ErrorCode { get; set; } = "";
}
