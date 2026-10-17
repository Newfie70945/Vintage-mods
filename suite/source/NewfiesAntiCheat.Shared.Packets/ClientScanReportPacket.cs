using System;
using System.Collections.Generic;
using NewfiesAntiCheat.Shared.Models;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientScanReportPacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string ChallengeId { get; set; } = "";

	[ProtoMember(3)]
	public byte[] Nonce { get; set; } = Array.Empty<byte>();

	[ProtoMember(4)]
	public long ScanStartedUtc { get; set; }

	[ProtoMember(5)]
	public long ScanCompletedUtc { get; set; }

	[ProtoMember(6)]
	public int CompletedScanners { get; set; }

	[ProtoMember(7)]
	public List<ClientModDto> Mods { get; set; } = new List<ClientModDto>();

	[ProtoMember(8)]
	public List<ScanFindingDto> Findings { get; set; } = new List<ScanFindingDto>();

	[ProtoMember(9)]
	public string ClientVersion { get; set; } = "";

	[ProtoMember(10)]
	public string ReportHash { get; set; } = "";

	[ProtoMember(11)]
	public List<ClientAssemblyDto> Assemblies { get; set; } = new List<ClientAssemblyDto>();

	[ProtoMember(12)]
	public ClientIntegrityDto? Integrity { get; set; }

	[ProtoMember(13)]
	public ClientRuntimeDto? Runtime { get; set; }

	[ProtoMember(14)]
	public List<ClientScannerMetricDto> ScannerMetrics { get; set; } = new List<ClientScannerMetricDto>();

	[ProtoMember(15)]
	public ClientHarmonyDto? Harmony { get; set; }

	[ProtoMember(16)]
	public int ReportHashVersion { get; set; }

	[ProtoMember(17)]
	public List<SelfAttestationResultDto> SelfAttestationResults { get; set; } = new List<SelfAttestationResultDto>();

	[ProtoMember(18)]
	public string SelfAttestationDigest { get; set; } = "";
}
