using System;
using System.Collections.Generic;
using BlockLogger.Models;

namespace BlockLogger.Admin;

public sealed class AdminState
{
    public int SchemaVersion { get; set; } = 3;
    public Dictionary<string, PlayerAdminRecord> Players { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AdminAuditRecord> Audit { get; set; } = new();
    public bool MaintenanceMode { get; set; }
    public string MaintenanceReason { get; set; } = "Maintenance";
    public long MaintenanceUntilUnixMilliseconds { get; set; }
    public Dictionary<string, List<AdminInventorySnapshotRecord>> InventorySnapshots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AdminOfflineInventoryRecord> OfflineInventories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlayerAdminRecord
{
    public string PlayerUid { get; set; } = "";
    public string LastKnownName { get; set; } = "";
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public long TotalPlaySeconds { get; set; }
    public long SessionStartedUnixMilliseconds { get; set; }
    public List<AdminWarningRecord> Warnings { get; set; } = new();
    public List<AdminNoteRecord> Notes { get; set; } = new();
    public long MuteUntilUnixMilliseconds { get; set; }
    public bool PermanentMute { get; set; }
    public string MuteReason { get; set; } = "";
    public string MutedBy { get; set; } = "";
    public long BanUntilUnixMilliseconds { get; set; }
    public string BanReason { get; set; } = "";
    public string BannedBy { get; set; } = "";
    public bool Frozen { get; set; }
    public long FreezeUntilUnixMilliseconds { get; set; }
    public string FrozenBy { get; set; } = "";
    public string FreezeReason { get; set; } = "";
}

public sealed class AdminWarningRecord
{
    public long Id { get; set; }
    public long UnixMilliseconds { get; set; }
    public string IssuedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Removed { get; set; }
    public string RemovedBy { get; set; } = "";
    public long RemovedUnixMilliseconds { get; set; }
}

public sealed class AdminNoteRecord
{
    public long Id { get; set; }
    public long UnixMilliseconds { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Removed { get; set; }
}

public sealed class AdminAuditRecord
{
    public long Id { get; set; }
    public long UnixMilliseconds { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetUid { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "ingame";
}

public sealed class AdminPlayerSummary
{
    public string PlayerUid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Nickname { get; set; } = "";
    public bool Online { get; set; }
    public string Role { get; set; } = "";
    public string GameMode { get; set; } = "";
    public int PingMilliseconds { get; set; }
    public long FirstSeenUnixMilliseconds { get; set; }
    public long LastSeenUnixMilliseconds { get; set; }
    public long SessionSeconds { get; set; }
    public long TotalPlaySeconds { get; set; }
    public int ActiveWarnings { get; set; }
    public bool Muted { get; set; }
    public long MuteUntilUnixMilliseconds { get; set; }
    public bool Banned { get; set; }
    public long BanUntilUnixMilliseconds { get; set; }
    public bool Frozen { get; set; }
    public bool Jailed { get; set; }
    public long JailUntilUnixMilliseconds { get; set; }
    public bool Afk { get; set; }
    public long IdleSeconds { get; set; }
    public string Position { get; set; } = "";
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public bool PlayerListClientDetected { get; set; }
    public string PlayerListClientVersion { get; set; } = "";
}

public sealed class AdminInventorySlotView
{
    public string Inventory { get; set; } = "";
    public string Section { get; set; } = "Other";
    public int Slot { get; set; }
    public bool Empty { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public int StackSize { get; set; }
    public int MaxStackSize { get; set; }
    public int MaxSlotStackSize { get; set; }
    public int Durability { get; set; }
    public int MaxDurability { get; set; }
    public float Temperature { get; set; }
    public string StorageType { get; set; } = "";
    public string Fingerprint { get; set; } = "empty";
    // Full serialized stack for the read-only Shift+L inventory viewer. This preserves
    // stack attributes used by meals, containers, variants, durability, and modded items.
    [System.Text.Json.Serialization.JsonIgnore]
    public string SerializedStack { get; set; } = "";
}

// Read-only investigation data is independent of the limited rollback snapshot history.
public sealed class AdminOfflineInventoryRecord
{
    public long CapturedUnixMilliseconds { get; set; }
    public string Source { get; set; } = "disconnect";
    public List<AdminInventorySlotView> Slots { get; set; } = new();
}

public sealed class AdminInventoryViewStatus
{
    public bool Available { get; set; }
    public bool ReadOnly { get; set; } = true;
    public string Source { get; set; } = "unavailable";
    public long CapturedUnixMilliseconds { get; set; }
    public long ReadUnixMilliseconds { get; set; }
    public long SnapshotId { get; set; }
    public bool Partial { get; set; }
    public string Message { get; set; } = "No saved inventory is available for this player.";
}

public sealed class AdminInventoryInspection
{
    public List<AdminInventorySlotView> Slots { get; set; } = new();
    public AdminInventoryViewStatus Status { get; set; } = new();
}

public sealed class AdminItemSearchResult
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string ItemClass { get; set; } = "";
    public int MaxStackSize { get; set; }
}

public sealed class AdminInventorySnapshotSlot
{
    public string Inventory { get; set; } = "";
    public int Slot { get; set; }
    public string StackBase64 { get; set; } = ""; // Legacy JSON migration only.
    public byte[] StackBytes { get; set; } = Array.Empty<byte>();
    public string ItemCode { get; set; } = "";
    public int StackSize { get; set; }
}

public sealed class AdminInventorySnapshotRecord
{
    public long Id { get; set; }
    public long UnixMilliseconds { get; set; }
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public int OccupiedSlots { get; set; }
    public List<AdminInventorySnapshotSlot> Slots { get; set; } = new();
    // Persisted snapshots retain only their header in the running server. Stack
    // attributes are read on demand, without keeping every historical blob in RAM.
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SlotsPayloadLoaded { get; set; } = true;
}

public sealed class AdminInventoryDiffRow
{
    public string Inventory { get; set; } = "";
    public int Slot { get; set; }
    public string SnapshotItem { get; set; } = "";
    public string CurrentItem { get; set; } = "";
    public string Change { get; set; } = "";
}

public sealed class LagHereReport
{
    public int RadiusChunks { get; set; }
    public int Entities { get; set; }
    public int DroppedItems { get; set; }
    public int Players { get; set; }
    public int LoadedChunks { get; set; }
    public double MainThreadDelayMs { get; set; }
    public double CpuPercent { get; set; }
    public int BlockLoggerQueue { get; set; }
    public Dictionary<string, int> TopEntityTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CleanupReport
{
    public int LoadedEntities { get; set; }
    public int Creatures { get; set; }
    public int InanimateEntities { get; set; }
    public int DeadOrDespawning { get; set; }
    public int DroppedItems { get; set; }
    public int Removed { get; set; }
    public bool DryRun { get; set; }
}
public sealed class AdminPlayerDetailSnapshot
{
    public AdminPlayerSummary Player { get; set; } = new();
    public List<AdminWarningRecord> Warnings { get; set; } = new();
    public List<AdminNoteRecord> Notes { get; set; } = new();
    public List<AdminAuditRecord> History { get; set; } = new();
    public List<AdminInventorySlotView> Inventory { get; set; } = new();
    public AdminInventoryViewStatus InventoryStatus { get; set; } = new();
    public List<AdminInventorySnapshotRecord> InventorySnapshots { get; set; } = new();
    public List<LogEventRecord> BlockHistory { get; set; } = new();
}


public sealed class AdminInventoryDetailSnapshot
{
    public AdminPlayerSummary Player { get; set; } = new();
    public List<AdminInventorySlotView> Inventory { get; set; } = new();
    public AdminInventoryViewStatus InventoryStatus { get; set; } = new();
    public List<AdminInventorySnapshotRecord> InventorySnapshots { get; set; } = new();
    public List<AdminAuditRecord> History { get; set; } = new();
}

public sealed class AdminStorageHealth
{
    public bool Available { get; set; }
    public string Path { get; set; } = "";
    public long DatabaseBytes { get; set; }
    public long WalBytes { get; set; }
    public long ShmBytes { get; set; }
    public long SaveCount { get; set; }
    public long LastSaveUnixMilliseconds { get; set; }
    public long LastSaveMilliseconds { get; set; }
    public long LastBackupUnixMilliseconds { get; set; }
    public int BackupCount { get; set; }
    public long LastIntegrityUnixMilliseconds { get; set; }
    public long LastIntegrityMilliseconds { get; set; }
    public string Integrity { get; set; } = "Not run";
    public string LastError { get; set; } = "";
    public long LastErrorUnixMilliseconds { get; set; }
    public int Players { get; set; }
    public int AuditRows { get; set; }
    public int InventorySnapshots { get; set; }
    public bool Dirty { get; set; }
}
