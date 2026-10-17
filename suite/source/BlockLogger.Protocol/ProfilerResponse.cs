using System.Collections.Generic;
using ProtoBuf;

namespace BlockLogger.Protocol;

[ProtoContract]
public sealed class ProfilerResponse
{
	[ProtoMember(1)]
	public long RequestId { get; set; }

	[ProtoMember(2)]
	public bool Success { get; set; }

	[ProtoMember(3)]
	public string Message { get; set; } = "";

	[ProtoMember(4)]
	public List<ProfilerSamplePacket> Samples { get; set; } = new List<ProfilerSamplePacket>();

	[ProtoMember(5)]
	public List<ProfilerSlowSpikePacket> SlowSpikes { get; set; } = new List<ProfilerSlowSpikePacket>();

	[ProtoMember(6)]
	public List<ProfilerHotspotPacket> Hotspots { get; set; } = new List<ProfilerHotspotPacket>();

	[ProtoMember(7)]
	public bool ProfileRunning { get; set; }

	[ProtoMember(8)]
	public int ProfileDurationSeconds { get; set; }

	[ProtoMember(9)]
	public int ProfileRemainingSeconds { get; set; }

	[ProtoMember(10)]
	public ProfilerSamplePacket Current { get; set; } = new ProfilerSamplePacket();

	[ProtoMember(11)]
	public ProfilerProfileSummaryPacket LastProfile { get; set; } = new ProfilerProfileSummaryPacket();

	[ProtoMember(12)]
	public bool Enabled { get; set; }

	[ProtoMember(13)]
	public List<ProfilerModHotspotPacket> ModHotspots { get; set; } = new List<ProfilerModHotspotPacket>();

	[ProtoMember(14)]
	public List<ProfilerPhasePacket> Phases { get; set; } = new List<ProfilerPhasePacket>();

	[ProtoMember(15)]
	public List<ProfilerProfileSlowTickPacket> ProfileSlowTicks { get; set; } = new List<ProfilerProfileSlowTickPacket>();

	[ProtoMember(16)]
	public int LoadedModCount { get; set; }

	[ProtoMember(17)]
	public List<ProfilerOffThreadReportPacket> OffThreadReports { get; set; } = new List<ProfilerOffThreadReportPacket>();

	[ProtoMember(18)]
	public List<ProfilerDiagnosticIncidentPacket> DiagnosticIncidents { get; set; } = new List<ProfilerDiagnosticIncidentPacket>();

	[ProtoMember(19)]
	public ProfilerDiagnosticSummaryPacket DiagnosticSummary { get; set; } = new ProfilerDiagnosticSummaryPacket();

	[ProtoMember(20)]
	public string LocalWebUrl { get; set; } = "";

	[ProtoMember(21)]
	public string PublicWebUrl { get; set; } = "";

	[ProtoMember(22)]
	public bool Compact { get; set; }
}
