using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerSamplePacket
{
	[ProtoMember(1)]
	public long UnixMilliseconds { get; set; }

	[ProtoMember(2)]
	public double MainThreadDelayMs { get; set; }

	[ProtoMember(3)]
	public double CpuPercent { get; set; }

	[ProtoMember(4)]
	public double WorkingSetMb { get; set; }

	[ProtoMember(5)]
	public double ManagedHeapMb { get; set; }

	[ProtoMember(6)]
	public int Gen0Collections { get; set; }

	[ProtoMember(7)]
	public int Gen1Collections { get; set; }

	[ProtoMember(8)]
	public int Gen2Collections { get; set; }

	[ProtoMember(9)]
	public int Players { get; set; }

	[ProtoMember(10)]
	public int LoadedEntities { get; set; }

	[ProtoMember(11)]
	public int LoadedChunks { get; set; }

	[ProtoMember(12)]
	public int BlockLoggerQueue { get; set; }

	[ProtoMember(13)]
	public long BlockLoggerWriterMs { get; set; }

	[ProtoMember(14)]
	public long BlockLoggerDropped { get; set; }

	[ProtoMember(15)]
	public int AnimalTrackedEntities { get; set; }

	[ProtoMember(16)]
	public int AnimalTrackedRegions { get; set; }
}
