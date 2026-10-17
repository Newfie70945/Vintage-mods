using ProtoBuf;

namespace BlockLogger.Afk;

[ProtoContract]
public sealed class AfkClientHelloPacket
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = 1;
    [ProtoMember(2)] public string ClientVersion { get; set; } = SuiteRelease.Version;
}
