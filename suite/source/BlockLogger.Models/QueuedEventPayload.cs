using System;

namespace BlockLogger.Models;

public readonly struct QueuedEventPayload(DateTime timestampUtc, string ingameDate, LogActionType action, string playerUid, string playerName, string worldId, string dimension, int x, int y, int z, string blockCode, string? k1 = null, string? v1 = null, string? k2 = null, string? v2 = null, string? k3 = null, string? v3 = null, string? k4 = null, string? v4 = null, string? k5 = null, string? v5 = null, string? k6 = null, string? v6 = null, string? k7 = null, string? v7 = null, string? k8 = null, string? v8 = null)
{
	public DateTime TimestampUtc { get; } = timestampUtc;

	public string IngameDate { get; } = ingameDate;

	public LogActionType Action { get; } = action;

	public string PlayerUid { get; } = playerUid;

	public string PlayerName { get; } = playerName;

	public string WorldId { get; } = worldId;

	public string Dimension { get; } = dimension;

	public int X { get; } = x;

	public int Y { get; } = y;

	public int Z { get; } = z;

	public string BlockCode { get; } = blockCode;

	public string? K1 { get; } = k1;

	public string? V1 { get; } = v1;

	public string? K2 { get; } = k2;

	public string? V2 { get; } = v2;

	public string? K3 { get; } = k3;

	public string? V3 { get; } = v3;

	public string? K4 { get; } = k4;

	public string? V4 { get; } = v4;

	public string? K5 { get; } = k5;

	public string? V5 { get; } = v5;

	public string? K6 { get; } = k6;

	public string? V6 { get; } = v6;

	public string? K7 { get; } = k7;

	public string? V7 { get; } = v7;

	public string? K8 { get; } = k8;

	public string? V8 { get; } = v8;

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
