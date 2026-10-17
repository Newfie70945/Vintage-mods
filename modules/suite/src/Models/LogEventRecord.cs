using System;
using System.Collections.Generic;

namespace BlockLogger.Models;

public sealed class LogEventRecord
{
    [Newtonsoft.Json.JsonIgnore] public long StorageId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string IngameDate { get; set; } = string.Empty;
    public LogActionType Action { get; set; }
    public string PlayerUid { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;
    public string Dimension { get; set; } = "overworld";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public string BlockCode { get; set; } = string.Empty;
    public Dictionary<string, string> Meta { get; set; } = new();
}
