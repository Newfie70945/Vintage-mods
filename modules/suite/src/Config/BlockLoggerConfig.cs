using System;
using System.Collections.Generic;

namespace BlockLogger.Config;

public sealed class BlockLoggerConfig
{
    public const int CurrentSchemaVersion = 3;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Enabled { get; set; } = true;
    public bool LogBlockBreak { get; set; } = true;
    public bool LogBlockPlace { get; set; } = true;
    public bool LogBlockUse { get; set; } = true;
    public bool LogContainerAccess { get; set; } = true;
    public bool LogContainerItemChanges { get; set; } = true;
    public bool LogContainerSnapshots { get; set; } = false;
    public bool LogEntityInteract { get; set; } = true;
    public bool LogEntitySpawn { get; set; } = true;
    public bool LogEntityDeath { get; set; } = true;
    public bool LogEntityDespawn { get; set; } = true;
    public int FlushIntervalMs { get; set; } = 1000;
    public int MaxQueuedEvents { get; set; } = 5000;
    // Emergency in-memory reserve used when the normal audit queue is saturated.
    // This reduces record loss during short SQLite stalls without blocking game callbacks.
    public int EmergencyOverflowCapacity { get; set; } = 20000;
    public int RetentionDays { get; set; } = 180;
    // Full SQLite backups can be almost as large as the live database. Keep the
    // automatic schedule conservative by default and retain only a small safety set.
    // Set AutoDatabaseBackupIntervalHours to 0 to disable automatic backups; manual
    // backups remain available from the web dashboard.
    public int AutoDatabaseBackupIntervalHours { get; set; } = 72;
    public int DatabaseBackupRetentionCount { get; set; } = 2;
    public int SnapshotDebounceMs { get; set; } = 2000;
    public int MaxTrackedContainerSessions { get; set; } = 1024;
    public bool SnapshotOnSlotChange { get; set; } = false;
    public int DefaultDisplayCount { get; set; } = 10;
    public int DefaultEntityDisplayCount { get; set; } = 10;
    public int DefaultRadius { get; set; } = 5;
    public int EntityRadius { get; set; } = 3;
    public int EntityAggregateWindowMs { get; set; } = 10000;
    public bool AggregateEntitySpawnDespawn { get; set; } = false;
    public bool AdaptiveEntityAggregation { get; set; } = false;
    public int EntityAggregateMaxWindowMs { get; set; } = 30000;
    public bool EntityAggregateRegionBuckets { get; set; } = false;
    public int EntityAggregateRegionSize { get; set; } = 64;
    public int LoadShedQueuePercent { get; set; } = 50;
    public int LoadShedBatchMs { get; set; } = 75;
    public int BulkEntityPositionSampleIntervalMs { get; set; } = 0;
    public bool AutoMigrateLegacyLogs { get; set; } = false;

    // Server diagnostics/profiler is opt-in. Existing configs that predate this
    // setting also deserialize to false, so upgrades do not silently start sampling.
    public bool ProfilerEnabled { get; set; } = false;
    public int MaxRadius { get; set; } = 64;
    public int MaxDisplayCount { get; set; } = 100;
    public string LogFilePrefix { get; set; } = "blocklogger";
    public string CoordinateDisplayMode { get; set; } = "RelativeToDefaultSpawn";
    public string ContainerSnapshotFilePrefix { get; set; } = "container-snapshots";
    public List<string> IgnoredBlockCodePrefixes { get; set; } = new() { "game:tallgrass-", "game:water-" };
    public List<string> IgnoredEntityCodePrefixes { get; set; } = new();

    public void ApplyFrom(BlockLoggerConfig other)
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, other.SchemaVersion);
        Enabled = other.Enabled;
        LogBlockBreak = other.LogBlockBreak;
        LogBlockPlace = other.LogBlockPlace;
        LogBlockUse = other.LogBlockUse;
        LogContainerAccess = other.LogContainerAccess;
        LogContainerItemChanges = other.LogContainerItemChanges;
        LogContainerSnapshots = other.LogContainerSnapshots;
        LogEntityInteract = other.LogEntityInteract;
        LogEntitySpawn = other.LogEntitySpawn;
        LogEntityDeath = other.LogEntityDeath;
        LogEntityDespawn = other.LogEntityDespawn;
        FlushIntervalMs = other.FlushIntervalMs;
        MaxQueuedEvents = other.MaxQueuedEvents;
        EmergencyOverflowCapacity = other.EmergencyOverflowCapacity;
        RetentionDays = other.RetentionDays;
        AutoDatabaseBackupIntervalHours = other.AutoDatabaseBackupIntervalHours;
        DatabaseBackupRetentionCount = other.DatabaseBackupRetentionCount;
        SnapshotDebounceMs = other.SnapshotDebounceMs;
        MaxTrackedContainerSessions = other.MaxTrackedContainerSessions;
        SnapshotOnSlotChange = other.SnapshotOnSlotChange;
        DefaultDisplayCount = other.DefaultDisplayCount;
        DefaultEntityDisplayCount = other.DefaultEntityDisplayCount;
        DefaultRadius = other.DefaultRadius;
        EntityRadius = other.EntityRadius;
        EntityAggregateWindowMs = other.EntityAggregateWindowMs;
        AggregateEntitySpawnDespawn = other.AggregateEntitySpawnDespawn;
        AdaptiveEntityAggregation = other.AdaptiveEntityAggregation;
        EntityAggregateMaxWindowMs = other.EntityAggregateMaxWindowMs;
        EntityAggregateRegionBuckets = other.EntityAggregateRegionBuckets;
        EntityAggregateRegionSize = other.EntityAggregateRegionSize;
        LoadShedQueuePercent = other.LoadShedQueuePercent;
        LoadShedBatchMs = other.LoadShedBatchMs;
        BulkEntityPositionSampleIntervalMs = other.BulkEntityPositionSampleIntervalMs;
        AutoMigrateLegacyLogs = other.AutoMigrateLegacyLogs;
        ProfilerEnabled = other.ProfilerEnabled;
        MaxRadius = other.MaxRadius;
        MaxDisplayCount = other.MaxDisplayCount;
        LogFilePrefix = other.LogFilePrefix;
        CoordinateDisplayMode = other.CoordinateDisplayMode;
        ContainerSnapshotFilePrefix = other.ContainerSnapshotFilePrefix;
        IgnoredBlockCodePrefixes = new List<string>(other.IgnoredBlockCodePrefixes ?? new());
        IgnoredEntityCodePrefixes = new List<string>(other.IgnoredEntityCodePrefixes ?? new());
    }

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        FlushIntervalMs = Math.Clamp(FlushIntervalMs, 100, 60_000);
        MaxQueuedEvents = Math.Clamp(MaxQueuedEvents, 500, 250_000);
        EmergencyOverflowCapacity = Math.Clamp(EmergencyOverflowCapacity, 1_000, 1_000_000);
        RetentionDays = Math.Clamp(RetentionDays, 0, 3650);
        AutoDatabaseBackupIntervalHours = Math.Clamp(AutoDatabaseBackupIntervalHours, 0, 24 * 365);
        DatabaseBackupRetentionCount = Math.Clamp(DatabaseBackupRetentionCount, 1, 20);
        SnapshotDebounceMs = Math.Clamp(SnapshotDebounceMs, 250, 30_000);
        MaxTrackedContainerSessions = Math.Clamp(MaxTrackedContainerSessions, 16, 8192);
        LogFilePrefix = string.IsNullOrWhiteSpace(LogFilePrefix) ? "blocklogger" : LogFilePrefix.Trim();
        MaxDisplayCount = Math.Clamp(MaxDisplayCount, 1, 2000);
        DefaultDisplayCount = Math.Clamp(DefaultDisplayCount, 1, MaxDisplayCount);
        DefaultEntityDisplayCount = Math.Clamp(DefaultEntityDisplayCount, 1, MaxDisplayCount);
        DefaultRadius = Math.Clamp(DefaultRadius, 0, 256);
        EntityRadius = Math.Clamp(EntityRadius, 0, 256);
        EntityAggregateWindowMs = Math.Clamp(EntityAggregateWindowMs, 1000, 60_000);
        EntityAggregateMaxWindowMs = Math.Clamp(EntityAggregateMaxWindowMs, EntityAggregateWindowMs, 120_000);
        EntityAggregateRegionSize = Math.Clamp(EntityAggregateRegionSize, 8, 256);
        LoadShedQueuePercent = Math.Clamp(LoadShedQueuePercent, 25, 95);
        LoadShedBatchMs = Math.Clamp(LoadShedBatchMs, 25, 5000);
        BulkEntityPositionSampleIntervalMs = Math.Clamp(BulkEntityPositionSampleIntervalMs, 0, 60_000);
        MaxRadius = Math.Clamp(MaxRadius, 0, 512);
        IgnoredBlockCodePrefixes ??= new List<string>();
        IgnoredEntityCodePrefixes ??= new List<string>();
        CoordinateDisplayMode = string.IsNullOrWhiteSpace(CoordinateDisplayMode) ? "RelativeToDefaultSpawn" : CoordinateDisplayMode.Trim();
        ContainerSnapshotFilePrefix = string.IsNullOrWhiteSpace(ContainerSnapshotFilePrefix) ? "container-snapshots" : ContainerSnapshotFilePrefix.Trim();
    }
}
