using System;
using System.Collections.Generic;

namespace BlockLogger.Reports;

public sealed class PlayerReportRecord
{
    public long Id { get; set; }
    public long CreatedUnixMilliseconds { get; set; }
    public long UpdatedUnixMilliseconds { get; set; }
    public string Status { get; set; } = "Open";
    public string ReporterUid { get; set; } = "";
    public string ReporterName { get; set; } = "";
    public string TargetUid { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string AssignedStaff { get; set; } = "";
    public List<PlayerReportNote> Notes { get; set; } = new();
    public List<PlayerReportEvent> Events { get; set; } = new();
    public int NoteCount { get; set; }
    public int EventCount { get; set; }
    public int HistoryOffset { get; set; }
    public int HistoryLimit { get; set; }
}

public sealed class PlayerReportNote
{
    public long UnixMilliseconds { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class PlayerReportEvent
{
    public long UnixMilliseconds { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class PlayerReportSnapshot
{
    public long GeneratedUnixMilliseconds { get; set; }
    public int OpenCount { get; set; }
    public int InvestigatingCount { get; set; }
    public int ResolvedCount { get; set; }
    public int TotalCount { get; set; }
    public string RootCommandOwner { get; set; } = "Unknown";
    public bool RootCommandRegisteredBySuite { get; set; }
    public List<PlayerReportRecord> Reports { get; set; } = new();
    public bool StorageReady { get; set; }
    public string StorageError { get; set; } = "";
}

internal sealed class PlayerReportState
{
    public int SchemaVersion { get; set; } = 1;
    public long NextId { get; set; } = 1;
    public List<PlayerReportRecord> Reports { get; set; } = new();
}
