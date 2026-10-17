using System;
using System.Collections.Generic;

namespace NewfiesChatGuard;

public sealed class ChatGuardState
{
    public Dictionary<string, ChatGuardMuteRecord> Mutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ChatGuardWarningState> Warnings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChatGuardMuteRecord
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public long CreatedUtcMs { get; set; }
    public long ExpiresUtcMs { get; set; }
    public bool Permanent { get; set; }
    public string Reason { get; set; } = "";
    public string Actor { get; set; } = "ChatGuard";
}

public sealed class ChatGuardWarningState
{
    public string PlayerName { get; set; } = "";
    public List<long> WarningUtcMs { get; set; } = new();
}

public sealed class ChatGuardAuditEntry
{
    public long TimestampUtcMs { get; set; }
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public int ChannelId { get; set; }
    public string Rule { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string MessagePreview { get; set; } = "";
    public string Actor { get; set; } = "ChatGuard";
}

public sealed class ChatGuardDecision
{
    public bool Available { get; set; } = true;
    public bool Blocked { get; set; }
    public string Rule { get; set; } = "";
    public string Action { get; set; } = "Allow";
    public string Detail { get; set; } = "";
}

internal sealed class ChatSample
{
    public long TimestampUtcMs { get; init; }
    public string Normalized { get; init; } = "";
}

internal sealed class CachedDecision
{
    public long CreatedUtcMs { get; init; }
    public ChatGuardDecision Decision { get; init; } = new();
}
