using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ClientModDto
{
    [ProtoMember(1)] public string ModId { get; set; } = "";
    [ProtoMember(2)] public string Version { get; set; } = "";
    [ProtoMember(3)] public string ModType { get; set; } = "";
    [ProtoMember(4)] public string FileName { get; set; } = "";
    [ProtoMember(5)] public string Sha256 { get; set; } = "";
    [ProtoMember(6)] public bool IsCodeMod { get; set; }
    [ProtoMember(7)] public bool IsEnabled { get; set; } = true;
    [ProtoMember(8)] public string SourceType { get; set; } = "";
    [ProtoMember(9)] public string ContentSha256 { get; set; } = "";
    [ProtoMember(10)] public long PackageBytes { get; set; }
    [ProtoMember(11)] public int DllCount { get; set; }
    [ProtoMember(12)] public long DllBytes { get; set; }
}
