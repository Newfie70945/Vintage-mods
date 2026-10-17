using BlockLogger.Models;

namespace BlockLogger.Models;

/// <summary>
/// Entity-only queue payload. Keeps numeric values numeric and reuses cached entity-type
/// strings so the Vintage Story main thread does not allocate metadata strings/JSON for
/// every spawn/despawn/death event.
/// </summary>
public readonly struct QueuedEntityEventPayload
{
    public QueuedEntityEventPayload(
        long timestampTicks, string ingameDate, LogActionType action,
        string playerUid, string playerName, string worldId, string dimension,
        int x, int y, int z,
        long entityId, int entityTypeId, string entityCode, string entityClass,
        string? heldItem = null, int heldStackSize = 0,
        int damageTier = int.MinValue, string? damageType = null,
        long sourceEntityId = -1, string? sourceEntityCode = null,
        string? sourcePlayerUid = null, string? sourcePlayerName = null,
        double? sourceX = null, double? sourceY = null, double? sourceZ = null,
        string? despawnReason = null, int eventCount = 1)
    {
        TimestampTicks = timestampTicks;
        IngameDate = ingameDate;
        Action = action;
        PlayerUid = playerUid;
        PlayerName = playerName;
        WorldId = worldId;
        Dimension = dimension;
        X = x; Y = y; Z = z;
        EntityId = entityId;
        EntityTypeId = entityTypeId;
        EntityCode = entityCode;
        EntityClass = entityClass;
        HeldItem = heldItem;
        HeldStackSize = heldStackSize;
        DamageTier = damageTier;
        DamageType = damageType;
        SourceEntityId = sourceEntityId;
        SourceEntityCode = sourceEntityCode;
        SourcePlayerUid = sourcePlayerUid;
        SourcePlayerName = sourcePlayerName;
        SourceX = sourceX; SourceY = sourceY; SourceZ = sourceZ;
        DespawnReason = despawnReason;
        EventCount = eventCount < 1 ? 1 : eventCount;
    }

    public long TimestampTicks { get; }
    public string IngameDate { get; }
    public LogActionType Action { get; }
    public string PlayerUid { get; }
    public string PlayerName { get; }
    public string WorldId { get; }
    public string Dimension { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public long EntityId { get; }
    public int EntityTypeId { get; }
    public string EntityCode { get; }
    public string EntityClass { get; }
    public string? HeldItem { get; }
    public int HeldStackSize { get; }
    public int DamageTier { get; }
    public string? DamageType { get; }
    public long SourceEntityId { get; }
    public string? SourceEntityCode { get; }
    public string? SourcePlayerUid { get; }
    public string? SourcePlayerName { get; }
    public double? SourceX { get; }
    public double? SourceY { get; }
    public double? SourceZ { get; }
    public string? DespawnReason { get; }
    public int EventCount { get; }

    public bool IsBulkEntity => Action is LogActionType.EntitySpawn or LogActionType.EntityDespawn;
}
