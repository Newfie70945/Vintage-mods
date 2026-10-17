using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class ClientHelloPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public string ClientVersion { get; set; } = "";
    [ProtoMember(3)] public string GameVersion { get; set; } = "";
    [ProtoMember(4)] public string Platform { get; set; } = "";
    [ProtoMember(5)] public int SupportedScanners { get; set; }
}
