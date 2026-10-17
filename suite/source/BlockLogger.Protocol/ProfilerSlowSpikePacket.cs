using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerSlowSpikePacket
{
	[ProtoMember(1)]
	public long UnixMilliseconds { get; set; }

	[ProtoMember(2)]
	public double DelayMs { get; set; }

	[ProtoMember(3)]
	public double CpuPercent { get; set; }

	[ProtoMember(4)]
	public double WorkingSetMb { get; set; }

	[ProtoMember(5)]
	public int Players { get; set; }

	[ProtoMember(6)]
	public int LoadedEntities { get; set; }

	[ProtoMember(7)]
	public int LoadedChunks { get; set; }

	[ProtoMember(8)]
	public int BlockLoggerQueue { get; set; }

	[ProtoMember(9)]
	public long BlockLoggerWriterMs { get; set; }

	[ProtoMember(10)]
	public int Gen0Delta { get; set; }

	[ProtoMember(11)]
	public int Gen1Delta { get; set; }

	[ProtoMember(12)]
	public int Gen2Delta { get; set; }
}
