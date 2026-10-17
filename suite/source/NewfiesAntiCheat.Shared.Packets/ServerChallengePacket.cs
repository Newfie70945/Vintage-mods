using System;
using System.Collections.Generic;
using NewfiesAntiCheat.Shared.Models;
using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ServerChallengePacket
{
	[ProtoMember(1)]
	public int ProtocolVersion { get; set; }

	[ProtoMember(2)]
	public string ChallengeId { get; set; } = "";

	[ProtoMember(3)]
	public byte[] Nonce { get; set; } = Array.Empty<byte>();

	[ProtoMember(4)]
	public long ServerTimestampUtc { get; set; }

	[ProtoMember(5)]
	public int RequestedScanners { get; set; }

	[ProtoMember(6)]
	public int TimeoutSeconds { get; set; }

	[ProtoMember(7)]
	public List<string> SuspiciousRuntimeTerms { get; set; } = new List<string>();

	[ProtoMember(8)]
	public List<string> ProtectedAssetPatterns { get; set; } = new List<string>();

	[ProtoMember(9)]
	public string ProtectedAssetDigest { get; set; } = "";

	[ProtoMember(10)]
	public int ProtectedAssetCount { get; set; }

	[ProtoMember(11)]
	public List<ProtectedAssetChallengeDto> ProtectedAssets { get; set; } = new List<ProtectedAssetChallengeDto>();

	[ProtoMember(12)]
	public bool ScanHotkeys { get; set; } = true;

	[ProtoMember(13)]
	public bool ScanClientCommands { get; set; } = true;

	[ProtoMember(14)]
	public bool ScanProtectedAssets { get; set; } = true;

	[ProtoMember(15)]
	public List<string> BlacklistedRuntimeTerms { get; set; } = new List<string>();

	[ProtoMember(16)]
	public List<SelfAttestationProbeDto> SelfAttestationProbes { get; set; } = new List<SelfAttestationProbeDto>();

	[ProtoMember(17)]
	public string BuildManifestDigest { get; set; } = "";
}
