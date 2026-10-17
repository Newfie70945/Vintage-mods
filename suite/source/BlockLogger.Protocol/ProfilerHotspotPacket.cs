using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerHotspotPacket
{
	[ProtoMember(1)]
	public string Code { get; set; } = "";

	[ProtoMember(2)]
	public double TotalMs { get; set; }

	[ProtoMember(3)]
	public double MaxSingleMs { get; set; }

	[ProtoMember(4)]
	public long Calls { get; set; }

	[ProtoMember(5)]
	public double PercentOfProfileTickTime { get; set; }

	[ProtoMember(6)]
	public string ModId { get; set; } = "";

	[ProtoMember(7)]
	public string ModName { get; set; } = "";

	[ProtoMember(8)]
	public string AttributionConfidence { get; set; } = "Low";

	[ProtoMember(9)]
	public bool IsCore { get; set; }
}
