using ProtoBuf;

namespace NewfiesSailing;

/// <summary>Server to client: current squall wind multiplier so client visuals match server physics.</summary>
[ProtoContract]
public sealed class SailingSyncPacket
{
    [ProtoMember(1)] public double Squall { get; set; } = 1;
}
