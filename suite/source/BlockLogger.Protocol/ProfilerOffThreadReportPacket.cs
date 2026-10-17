using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerOffThreadReportPacket
{
	[ProtoMember(1)]
	public double DurationMs { get; set; }

	[ProtoMember(2)]
	public string Summary { get; set; } = "";

	[ProtoMember(3)]
	public string ModId { get; set; } = "";

	[ProtoMember(4)]
	public string Confidence { get; set; } = "Low";
}
