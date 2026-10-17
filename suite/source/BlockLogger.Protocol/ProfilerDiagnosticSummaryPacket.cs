using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerDiagnosticSummaryPacket
{
	[ProtoMember(1)]
	public string State { get; set; } = "Disabled";

	[ProtoMember(2)]
	public int WindowMinutes { get; set; } = 5;

	[ProtoMember(3)]
	public string Summary { get; set; } = "";

	[ProtoMember(4)]
	public int StallCount { get; set; }

	[ProtoMember(5)]
	public int SevereStallCount { get; set; }

	[ProtoMember(6)]
	public double WorstDelayMs { get; set; }

	[ProtoMember(7)]
	public double AverageCpuPercent { get; set; }

	[ProtoMember(8)]
	public double MaxCpuPercent { get; set; }

	[ProtoMember(9)]
	public int Gen2Collections { get; set; }

	[ProtoMember(10)]
	public int MaxBlockLoggerQueue { get; set; }

	[ProtoMember(11)]
	public long MaxBlockLoggerWriterMs { get; set; }

	[ProtoMember(12)]
	public long BlockLoggerDroppedIncrease { get; set; }

	[ProtoMember(13)]
	public int MaxLoadedEntities { get; set; }

	[ProtoMember(14)]
	public int MaxLoadedChunks { get; set; }
}
