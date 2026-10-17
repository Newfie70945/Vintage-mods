using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerModHotspotPacket
{
	[ProtoMember(1)]
	public string ModId { get; set; } = "";

	[ProtoMember(2)]
	public string ModName { get; set; } = "";

	[ProtoMember(3)]
	public double TotalMs { get; set; }

	[ProtoMember(4)]
	public double MaxSingleMs { get; set; }

	[ProtoMember(5)]
	public long Calls { get; set; }

	[ProtoMember(6)]
	public double PercentOfProfileTickTime { get; set; }

	[ProtoMember(7)]
	public string Confidence { get; set; } = "Low";

	[ProtoMember(8)]
	public string TopMarker { get; set; } = "";

	[ProtoMember(9)]
	public bool IsCore { get; set; }

	[ProtoMember(10)]
	public int SlowSegments { get; set; }
}
