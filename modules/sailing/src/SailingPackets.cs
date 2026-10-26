using ProtoBuf;

namespace NewfiesSailing;

/// <summary>Server to client: shared weather state so client visuals match server physics.</summary>
[ProtoContract]
public sealed class SailingSyncPacket
{
    [ProtoMember(1)] public double Squall { get; set; } = 1;
    [ProtoMember(2)] public bool TemporalStorm { get; set; }
    /// <summary>Server mod-time seconds, so clients can reproduce the storm's wind rotation.</summary>
    [ProtoMember(3)] public double Seconds { get; set; }
    [ProtoMember(4)] public double WindRotationAmplitude { get; set; } = 1.5;
    [ProtoMember(5)] public double WindBoost { get; set; } = 1;
    [ProtoMember(6)] public double SeasonFactor { get; set; } = 1;
    [ProtoMember(7)] public double Fog { get; set; }
}
