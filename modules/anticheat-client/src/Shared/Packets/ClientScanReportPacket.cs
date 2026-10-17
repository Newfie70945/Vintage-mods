using System.Collections.Generic;
using ProtoBuf;
using NewfiesAntiCheat.Shared.Models;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientScanReportPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string ChallengeId { get; set; } = "";
    [ProtoMember(3)] public byte[] Nonce { get; set; } = System.Array.Empty<byte>();
    [ProtoMember(4)] public long ScanStartedUtc { get; set; }
    [ProtoMember(5)] public long ScanCompletedUtc { get; set; }
    [ProtoMember(6)] public int CompletedScanners { get; set; }
    [ProtoMember(7)] public List<ClientModDto> Mods { get; set; } = new();
    [ProtoMember(8)] public List<ScanFindingDto> Findings { get; set; } = new();
    [ProtoMember(9)] public string ClientVersion { get; set; } = "";
    [ProtoMember(10)] public string ReportHash { get; set; } = "";
    [ProtoMember(11)] public List<ClientAssemblyDto> Assemblies { get; set; } = new();
    [ProtoMember(12)] public ClientIntegrityDto? Integrity { get; set; }
    [ProtoMember(13)] public ClientRuntimeDto? Runtime { get; set; }
    [ProtoMember(14)] public List<ClientScannerMetricDto> ScannerMetrics { get; set; } = new();
    [ProtoMember(15)] public ClientHarmonyDto? Harmony { get; set; }
    // Version 2 covers scan metadata/control fields as well as the legacy payload hash.
    // ProtoBuf ignores this field on older protocol-9 builds.
    [ProtoMember(16)] public int ReportHashVersion { get; set; }
    [ProtoMember(17)] public List<SelfAttestationResultDto> SelfAttestationResults { get; set; } = new();
    [ProtoMember(18)] public string SelfAttestationDigest { get; set; } = "";
}
