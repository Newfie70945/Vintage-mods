using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientScannerMetricDto
{
	[ProtoMember(1)]
	public string Scanner { get; set; } = "";

	[ProtoMember(2)]
	public long DurationMs { get; set; }

	[ProtoMember(3)]
	public bool Success { get; set; }

	[ProtoMember(4)]
	public int ModCount { get; set; }

	[ProtoMember(5)]
	public int AssemblyCount { get; set; }

	[ProtoMember(6)]
	public int FindingCount { get; set; }

	[ProtoMember(7)]
	public int CacheHits { get; set; }

	[ProtoMember(8)]
	public int CacheMisses { get; set; }
}
