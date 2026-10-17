using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ServerModEvidenceRequestPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string RequestId { get; set; } = "";

	[ProtoMember(3)]
	public string ModId { get; set; } = "";

	[ProtoMember(4)]
	public string ExpectedFileName { get; set; } = "";

	[ProtoMember(5)]
	public string ExpectedPackageSha256 { get; set; } = "";

	[ProtoMember(6)]
	public long MaxBytes { get; set; }

	[ProtoMember(7)]
	public int MaxUploadKbps { get; set; }

	[ProtoMember(8)]
	public string ModVersion { get; set; } = "";

	[ProtoMember(9)]
	public int DeclineTempBanHours { get; set; }

	[ProtoMember(10)]
	public string DeclineAction { get; set; } = "TempBan";

	[ProtoMember(11)]
	public string RequestedBy { get; set; } = "";

	[ProtoMember(12)]
	public string ReviewReason { get; set; } = "";
}
