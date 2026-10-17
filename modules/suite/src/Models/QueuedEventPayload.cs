using System;

namespace BlockLogger.Models;

/// <summary>
/// Compact immutable payload copied on the Vintage Story server thread.
/// It intentionally contains no game API objects and no Dictionary allocation,
/// so it is safe for storage workers to process later.
/// </summary>
public readonly struct QueuedEventPayload
{
    public QueuedEventPayload(
        DateTime timestampUtc, string ingameDate, LogActionType action,
        string playerUid, string playerName, string worldId, string dimension,
        int x, int y, int z, string blockCode,
        string? k1 = null, string? v1 = null,
        string? k2 = null, string? v2 = null,
        string? k3 = null, string? v3 = null,
        string? k4 = null, string? v4 = null,
        string? k5 = null, string? v5 = null,
        string? k6 = null, string? v6 = null,
        string? k7 = null, string? v7 = null,
        string? k8 = null, string? v8 = null)
    {
        TimestampUtc = timestampUtc;
        IngameDate = ingameDate;
        Action = action;
        PlayerUid = playerUid;
        PlayerName = playerName;
        WorldId = worldId;
        Dimension = dimension;
        X = x; Y = y; Z = z;
        BlockCode = blockCode;
        K1 = k1; V1 = v1; K2 = k2; V2 = v2; K3 = k3; V3 = v3; K4 = k4; V4 = v4;
        K5 = k5; V5 = v5; K6 = k6; V6 = v6; K7 = k7; V7 = v7; K8 = k8; V8 = v8;
    }

    public DateTime TimestampUtc { get; }
    public string IngameDate { get; }
    public LogActionType Action { get; }
    public string PlayerUid { get; }
    public string PlayerName { get; }
    public string WorldId { get; }
    public string Dimension { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public string BlockCode { get; }
    public string? K1 { get; } public string? V1 { get; }
    public string? K2 { get; } public string? V2 { get; }
    public string? K3 { get; } public string? V3 { get; }
    public string? K4 { get; } public string? V4 { get; }
    public string? K5 { get; } public string? V5 { get; }
    public string? K6 { get; } public string? V6 { get; }
    public string? K7 { get; } public string? V7 { get; }
    public string? K8 { get; } public string? V8 { get; }

    public bool IsBulkEntity => Action is LogActionType.EntitySpawn or LogActionType.EntityDespawn;
}
