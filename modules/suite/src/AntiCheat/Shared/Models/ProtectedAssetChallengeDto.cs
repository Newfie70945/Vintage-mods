using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Models;

[ProtoContract]
public sealed class ProtectedAssetChallengeDto
{
    [ProtoMember(1)] public string Location { get; set; } = "";
    [ProtoMember(2)] public string Sha256 { get; set; } = "";
}
