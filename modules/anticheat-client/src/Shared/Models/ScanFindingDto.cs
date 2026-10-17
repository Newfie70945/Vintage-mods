using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ScanFindingDto
{
    [ProtoMember(1)] public string Scanner { get; set; } = "";
    [ProtoMember(2)] public string Code { get; set; } = "";
    [ProtoMember(3)] public int Severity { get; set; }
    [ProtoMember(4)] public string Summary { get; set; } = "";
    [ProtoMember(5)] public string Evidence { get; set; } = "";
    [ProtoMember(6)] public string Subject { get; set; } = "";
}
