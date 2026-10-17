namespace BlockLogger.Models;

public readonly struct QueuedEntityEventPayload(long timestampTicks, string ingameDate, LogActionType action, string playerUid, string playerName, string worldId, string dimension, int x, int y, int z, long entityId, int entityTypeId, string entityCode, string entityClass, string? heldItem = null, int heldStackSize = 0, int damageTier = int.MinValue, string? damageType = null, long sourceEntityId = -1L, string? sourceEntityCode = null, string? sourcePlayerUid = null, string? sourcePlayerName = null, double? sourceX = null, double? sourceY = null, double? sourceZ = null, string? despawnReason = null, int eventCount = 1)
{
	public long TimestampTicks { get; } = timestampTicks;

	public string IngameDate { get; } = ingameDate;

	public LogActionType Action { get; } = action;

	public string PlayerUid { get; } = playerUid;

	public string PlayerName { get; } = playerName;

	public string WorldId { get; } = worldId;

	public string Dimension { get; } = dimension;

	public int X { get; } = x;

	public int Y { get; } = y;

	public int Z { get; } = z;

	public long EntityId { get; } = entityId;

	public int EntityTypeId { get; } = entityTypeId;

	public string EntityCode { get; } = entityCode;

	public string EntityClass { get; } = entityClass;

	public string? HeldItem { get; } = heldItem;

	public int HeldStackSize { get; } = heldStackSize;

	public int DamageTier { get; } = damageTier;

	public string? DamageType { get; } = damageType;

	public long SourceEntityId { get; } = sourceEntityId;

	public string? SourceEntityCode { get; } = sourceEntityCode;

	public string? SourcePlayerUid { get; } = sourcePlayerUid;

	public string? SourcePlayerName { get; } = sourcePlayerName;

	public double? SourceX { get; } = sourceX;

	public double? SourceY { get; } = sourceY;

	public double? SourceZ { get; } = sourceZ;

	public string? DespawnReason { get; } = despawnReason;

	public int EventCount { get; } = (eventCount < 1) ? 1 : eventCount;

	public bool IsBulkEntity
	{
		get
		{
			LogActionType action = Action;
			if (action == LogActionType.EntitySpawn || action == LogActionType.EntityDespawn)
			{
				return true;
			}
			return false;
		}
	}
}
