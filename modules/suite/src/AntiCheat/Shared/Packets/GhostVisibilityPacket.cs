using ProtoBuf;

namespace NewfiesAntiCheat.Shared.Packets;

[ProtoContract]
public sealed class GhostVisibilityPacket
{
    [ProtoMember(1)] public long EntityId { get; set; }
    [ProtoMember(2)] public string PlayerUid { get; set; } = "";
    [ProtoMember(3)] public bool IsGhosted { get; set; }
    [ProtoMember(4)] public double X { get; set; }
    [ProtoMember(5)] public double Y { get; set; }
    [ProtoMember(6)] public double Z { get; set; }
    [ProtoMember(7)] public int Dimension { get; set; }
    [ProtoMember(8)] public float Yaw { get; set; }
    [ProtoMember(9)] public float Pitch { get; set; }
}
