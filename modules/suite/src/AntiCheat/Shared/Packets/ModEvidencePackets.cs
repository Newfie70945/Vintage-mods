using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ServerModEvidenceRequestPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public string ModId { get; set; } = "";
    [ProtoMember(4)] public string ExpectedFileName { get; set; } = "";
    [ProtoMember(5)] public string ExpectedPackageSha256 { get; set; } = "";
    [ProtoMember(6)] public long MaxBytes { get; set; }
    // Bounded upload-rate setting retained from the earlier evidence transport.
    // Protocol 13 keeps this limit after the player authorizes the transfer.
    [ProtoMember(7)] public int MaxUploadKbps { get; set; }
    // Protocol-13 authorization metadata shown to the player before any package bytes are read or sent.
    [ProtoMember(8)] public string ModVersion { get; set; } = "";
    [ProtoMember(9)] public int DeclineTempBanHours { get; set; }
    [ProtoMember(10)] public string DeclineAction { get; set; } = "TempBan";
    [ProtoMember(11)] public string RequestedBy { get; set; } = "";
    [ProtoMember(12)] public string ReviewReason { get; set; } = "";
}

[ProtoContract]
public sealed class ClientModEvidenceConsentPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public bool Accepted { get; set; }
    [ProtoMember(4)] public long DecisionUtc { get; set; }
}

[ProtoContract]
public sealed class ServerModEvidenceConsentAckPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public bool Authorized { get; set; }
    [ProtoMember(4)] public string Message { get; set; } = "";
}

[ProtoContract]
public sealed class ClientModEvidenceStartPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public string ModId { get; set; } = "";
    [ProtoMember(4)] public string OriginalFileName { get; set; } = "";
    [ProtoMember(5)] public long TotalBytes { get; set; }
    [ProtoMember(6)] public string Sha256 { get; set; } = "";
    [ProtoMember(7)] public bool GeneratedArchive { get; set; }
    [ProtoMember(8)] public int ChunkSize { get; set; }
    [ProtoMember(9)] public int TotalChunks { get; set; }
}

[ProtoContract]
public sealed class ClientModEvidenceChunkPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public int ChunkIndex { get; set; }
    [ProtoMember(4)] public byte[] Data { get; set; } = System.Array.Empty<byte>();
}

[ProtoContract]
public sealed class ClientModEvidenceCompletePacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string RequestId { get; set; } = "";
    [ProtoMember(3)] public bool Success { get; set; }
    [ProtoMember(4)] public string Message { get; set; } = "";
    [ProtoMember(5)] public long TotalBytes { get; set; }
    [ProtoMember(6)] public string Sha256 { get; set; } = "";
}
