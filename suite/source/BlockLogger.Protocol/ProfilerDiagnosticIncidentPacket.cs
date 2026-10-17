using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerDiagnosticIncidentPacket
{
	[ProtoMember(1)]
	public long UnixMilliseconds { get; set; }

	[ProtoMember(2)]
	public string Severity { get; set; } = "Watch";

	[ProtoMember(3)]
	public double DelayMs { get; set; }

	[ProtoMember(4)]
	public double CpuPercent { get; set; }

	[ProtoMember(5)]
	public double ManagedHeapMb { get; set; }

	[ProtoMember(6)]
	public int LoadedEntities { get; set; }

	[ProtoMember(7)]
	public int EntityDelta { get; set; }

	[ProtoMember(8)]
	public int LoadedChunks { get; set; }

	[ProtoMember(9)]
	public int ChunkDelta { get; set; }

	[ProtoMember(10)]
	public int BlockLoggerQueue { get; set; }

	[ProtoMember(11)]
	public long BlockLoggerWriterMs { get; set; }

	[ProtoMember(12)]
	public long BlockLoggerDroppedDelta { get; set; }

	[ProtoMember(13)]
	public int Gen0Delta { get; set; }

	[ProtoMember(14)]
	public int Gen1Delta { get; set; }

	[ProtoMember(15)]
	public int Gen2Delta { get; set; }

	[ProtoMember(16)]
	public string Evidence { get; set; } = "";
}
