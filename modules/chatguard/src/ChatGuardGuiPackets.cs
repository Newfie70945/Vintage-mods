using ProtoBuf;

namespace NewfiesChatGuard;

[ProtoContract]
public sealed class ChatGuardGuiRequest
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public string Operation { get; set; } = "snapshot";
    [ProtoMember(3)] public string Action { get; set; } = "";
    [ProtoMember(4)] public string Target { get; set; } = "";
    [ProtoMember(5)] public int Minutes { get; set; } = 10;
    [ProtoMember(6)] public bool Permanent { get; set; }
    [ProtoMember(7)] public string Reason { get; set; } = "";
    [ProtoMember(8)] public string ConfigJson { get; set; } = "";
    [ProtoMember(9)] public string TestMessage { get; set; } = "";
}

[ProtoContract]
public sealed class ChatGuardGuiResponse
{
    [ProtoMember(1)] public long RequestId { get; set; }
    [ProtoMember(2)] public bool Success { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
    [ProtoMember(4)] public string SnapshotJson { get; set; } = "{}";
    [ProtoMember(5)] public string TestJson { get; set; } = "{}";
    [ProtoMember(6)] public bool OpenDialog { get; set; }
    [ProtoMember(7)] public bool StandaloneGuiAllowed { get; set; } = true;
}
