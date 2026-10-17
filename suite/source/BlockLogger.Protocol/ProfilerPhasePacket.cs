using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerPhasePacket
{
	[ProtoMember(1)]
	public string Code { get; set; } = "";

	[ProtoMember(2)]
	public double TotalMs { get; set; }

	[ProtoMember(3)]
	public double AveragePerTickMs { get; set; }

	[ProtoMember(4)]
	public double MaxSingleMs { get; set; }

	[ProtoMember(5)]
	public long Calls { get; set; }

	[ProtoMember(6)]
	public double PercentOfProfileTickTime { get; set; }

	[ProtoMember(7)]
	public string ModId { get; set; } = "";

	[ProtoMember(8)]
	public string AttributionConfidence { get; set; } = "Low";
}
