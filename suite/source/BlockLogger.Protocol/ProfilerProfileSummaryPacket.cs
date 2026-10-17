using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerProfileSummaryPacket
{
	[ProtoMember(1)]
	public long CompletedUnixMilliseconds { get; set; }

	[ProtoMember(2)]
	public int DurationSeconds { get; set; }

	[ProtoMember(3)]
	public int TickCount { get; set; }

	[ProtoMember(4)]
	public double AverageTickMs { get; set; }

	[ProtoMember(5)]
	public double P95TickMs { get; set; }

	[ProtoMember(6)]
	public double P99TickMs { get; set; }

	[ProtoMember(7)]
	public double MaxTickMs { get; set; }

	[ProtoMember(8)]
	public int AttributedSegments { get; set; }

	[ProtoMember(9)]
	public int UnattributedSegments { get; set; }

	[ProtoMember(10)]
	public double AttributedMs { get; set; }

	[ProtoMember(11)]
	public double UnattributedMs { get; set; }
}
