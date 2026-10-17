using System.Collections.Generic;
using ProtoBuf;

namespace NewfiesPlayerList;

[ProtoContract]
public sealed class PublicPlayerListRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
}

[ProtoContract]
public sealed class PublicPlayerListClientHello
{
    [ProtoMember(1)] public string ClientVersion { get; set; } = "";
    [ProtoMember(2)] public int ProtocolVersion { get; set; } = 1;
    [ProtoMember(3)] public string Capabilities { get; set; } = "";
}

[ProtoContract]
public sealed class PublicPlayerListResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string ServerName { get; set; } = "";
    [ProtoMember(3)] public int MaxPlayers { get; set; }
    [ProtoMember(4)] public List<PublicPlayerListEntry> Players { get; set; } = new();
    [ProtoMember(5)] public float BackgroundOpacity { get; set; } = 0.72f;
    [ProtoMember(6)] public bool ShowRoles { get; set; } = true;
    [ProtoMember(7)] public bool ShowPing { get; set; } = true;
    [ProtoMember(8)] public int StyleVersion { get; set; } = 5;
    [ProtoMember(9)] public string BackendError { get; set; } = "";
    [ProtoMember(10)] public string BackendBuild { get; set; } = "";
    [ProtoMember(11)] public bool ShowSessionTime { get; set; } = true;
    [ProtoMember(12)] public string RosterSource { get; set; } = "";
    [ProtoMember(13)] public float LayoutScale { get; set; } = 1.0f;
    [ProtoMember(14)] public int RowsPerColumn { get; set; } = 8;
    [ProtoMember(15)] public int MaxColumns { get; set; } = 4;
    [ProtoMember(16)] public string Alignment { get; set; } = "CenterTop";
    [ProtoMember(17)] public int OffsetX { get; set; }
    [ProtoMember(18)] public int OffsetY { get; set; } = 22;
    [ProtoMember(19)] public bool ShowPlayerInitials { get; set; } = true;
    [ProtoMember(20)] public bool ShowCapacityBar { get; set; } = true;
    [ProtoMember(21)] public bool ShowColumnHeaders { get; set; }
    [ProtoMember(22)] public bool CompactRows { get; set; }
    [ProtoMember(23)] public bool ShowPingBars { get; set; } = true;
    [ProtoMember(24)] public bool ShowFooterHint { get; set; }
    [ProtoMember(25)] public string AccentHex { get; set; } = "#D6AE63";
}

[ProtoContract]
public sealed class PublicPlayerListEntry
{
    [ProtoMember(1)] public string PlayerName { get; set; } = "";
    [ProtoMember(2)] public string RoleName { get; set; } = "";
    // Proto tags 3 and 4 are intentionally reserved from the original protocol.
    [ProtoMember(5)] public int PingMs { get; set; } = -1;
    [ProtoMember(6)] public long SessionSeconds { get; set; }
    [ProtoMember(7)] public bool Afk { get; set; }
    [ProtoMember(8)] public string NameColorHex { get; set; } = "";
}
