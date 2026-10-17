using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using BlockLogger.Services;

namespace BlockLogger.Admin;

/// <summary>
/// Crash-safe, coalesced persistence for player administration state.
/// The game thread only marks the state dirty; SQLite work happens on a low-priority worker.
/// </summary>
public sealed class AdminStorageService : IDisposable
{
    private const int CurrentSchema = 1;
    private readonly ICoreServerAPI api;
    private readonly Func<AdminState> snapshotProvider;
    private readonly string dbPath;
    private readonly string backupsPath;
    private readonly AutoResetEvent saveSignal = new(false);
    private readonly CancellationTokenSource cts = new();
    private readonly Thread writerThread;
    private readonly object flushGate = new();
    private volatile bool dirty;
    private volatile bool disposed;
    private long lastSaveUnixMilliseconds;
    private long lastSaveMilliseconds;
    private long lastBackupUnixMilliseconds;
    private long lastIntegrityUnixMilliseconds;
    private long lastIntegrityMilliseconds;
    private string lastIntegrityResult = "Not run";
    private string lastError = "";
    private long lastErrorUnixMilliseconds;
    private long saveCount;
    private long persistedWarningHighWatermark;
    private long persistedNoteHighWatermark;
    private long persistedAuditHighWatermark;
    private long persistedSnapshotHighWatermark;
    private readonly Dictionary<string, long> persistedOfflineInventoryTimes = new(StringComparer.OrdinalIgnoreCase);

    public AdminStorageService(ICoreServerAPI api, Func<AdminState> snapshotProvider)
    {
        this.api = api;
        this.snapshotProvider = snapshotProvider;
        var worldRoot = SuiteDataPaths.GetWorldRoot(api);
        dbPath = Path.Combine(worldRoot, "essentials.db");
        backupsPath = Path.Combine(worldRoot, "backups");
        Directory.CreateDirectory(backupsPath);

        RecoverCorruptDatabaseIfNeeded();
        InitializeAndMigrate();
        LoadPersistedHighWatermarks();
        RunQuickCheck();

        writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "NSE Admin SQLite Writer",
            Priority = ThreadPriority.BelowNormal
        };
        writerThread.Start();
    }

    public string DatabasePath => dbPath;
    public long LastSaveUnixMilliseconds => Interlocked.Read(ref lastSaveUnixMilliseconds);
    public long LastSaveMilliseconds => Interlocked.Read(ref lastSaveMilliseconds);
    public long SaveCount => Interlocked.Read(ref saveCount);
    public long LastBackupUnixMilliseconds => Interlocked.Read(ref lastBackupUnixMilliseconds);
    public long LastIntegrityUnixMilliseconds => Interlocked.Read(ref lastIntegrityUnixMilliseconds);
    public long LastIntegrityMilliseconds => Interlocked.Read(ref lastIntegrityMilliseconds);
    public string LastIntegrityResult => Volatile.Read(ref lastIntegrityResult) ?? "Not run";
    public string LastError => Volatile.Read(ref lastError) ?? "";
    public long LastErrorUnixMilliseconds => Interlocked.Read(ref lastErrorUnixMilliseconds);
    public event Action<long>? InventorySnapshotsCommitted;

    public AdminStorageHealth GetHealth()
    {
        var health = new AdminStorageHealth
        {
            Available = true, Path = dbPath,
            DatabaseBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
            WalBytes = File.Exists(dbPath + "-wal") ? new FileInfo(dbPath + "-wal").Length : 0,
            ShmBytes = File.Exists(dbPath + "-shm") ? new FileInfo(dbPath + "-shm").Length : 0,
            SaveCount = SaveCount, LastSaveUnixMilliseconds = LastSaveUnixMilliseconds, LastSaveMilliseconds = LastSaveMilliseconds,
            LastBackupUnixMilliseconds = LastBackupUnixMilliseconds, BackupCount = Directory.Exists(backupsPath) ? Directory.EnumerateFiles(backupsPath, "essentials-*.db").Count(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)) : 0,
            LastIntegrityUnixMilliseconds = LastIntegrityUnixMilliseconds, LastIntegrityMilliseconds = LastIntegrityMilliseconds, Integrity = LastIntegrityResult,
            LastError = LastError, LastErrorUnixMilliseconds = LastErrorUnixMilliseconds, Dirty = dirty
        };
        return health;
    }

    public bool HasState()
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM players LIMIT 1) OR EXISTS(SELECT 1 FROM admin_audit LIMIT 1) OR EXISTS(SELECT 1 FROM inventory_snapshots LIMIT 1) OR EXISTS(SELECT 1 FROM meta WHERE key='maintenance_mode');";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        catch (Exception ex)
        {
            RecordError(ex);
            return false;
        }
    }

    public AdminState LoadState()
    {
        var state = new AdminState { SchemaVersion = 3 };
        try
        {
            using var conn = OpenConnection();
            LoadMeta(conn, state);
            LoadPlayers(conn, state);
            CloseStaleSessions(conn, state);
            LoadWarnings(conn, state);
            LoadNotes(conn, state);
            LoadAudit(conn, state);
            LoadSnapshots(conn, state);
            LoadOfflineInventories(conn, state);
        }
        catch (Exception ex)
        {
            RecordError(ex);
            api.Logger.Error("[ServerEssentials/Storage] Failed to load essentials.db: {0}", ex);
        }
        return state;
    }

    public void BackupLegacyJson(AdminState state)
    {
        try
        {
            var path = Path.Combine(backupsPath, $"legacy-admin-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(state, Formatting.Indented));
            api.Logger.Notification("[ServerEssentials/Storage] Backed up legacy admin JSON to {0}", path);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Storage] Could not back up legacy admin JSON: {0}", ex.Message);
        }
    }

    public void ImportLegacy(AdminState state)
    {
        PersistState(state);
        using var conn = OpenConnection();
        SetMeta(conn, "legacy_admin_imported", "1");
        SetMeta(conn, "legacy_admin_imported_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        CreateBackup("post-legacy-import");
    }

    public void RequestSave()
    {
        if (disposed) return;
        dirty = true;
        saveSignal.Set();
    }

    public void Flush()
    {
        if (disposed || !dirty) return;
        try { SaveDirtyState(); }
        catch (Exception ex)
        {
            RecordError(ex);
            api.Logger.Error("[ServerEssentials/Storage] Synchronous save failed: {0}", ex);
        }
    }

    private void SaveDirtyState()
    {
        lock (flushGate)
        {
            if (!dirty) return;
            // Clear before capture: a RequestSave arriving during capture/commit remains set.
            // Capture under the same gate as persistence, so an older background snapshot
            // cannot be queued behind a newer synchronous transaction and overwrite it.
            dirty = false;
            try
            {
                PersistState(snapshotProvider());
            }
            catch
            {
                dirty = true;
                throw;
            }
        }
    }

    public string RunQuickCheck()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
            sw.Stop();
            Volatile.Write(ref lastIntegrityResult, result);
            Interlocked.Exchange(ref lastIntegrityUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref lastIntegrityMilliseconds, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError(ex);
            Volatile.Write(ref lastIntegrityResult, "Failed");
            Interlocked.Exchange(ref lastIntegrityUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Interlocked.Exchange(ref lastIntegrityMilliseconds, sw.ElapsedMilliseconds);
            return "Failed: " + ex.Message;
        }
    }

    public string CreateBackup(string reason = "manual")
    {
        try
        {
            var cleanReason = new string((reason ?? "backup").Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(24).ToArray());
            if (string.IsNullOrWhiteSpace(cleanReason)) cleanReason = "backup";
            var targetPath = Path.Combine(backupsPath, $"essentials-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{cleanReason}.db");
            using var source = OpenConnection();
            try
            {
                using var maintenance = source.CreateCommand();
                maintenance.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
                maintenance.ExecuteNonQuery();
            }
            catch { }
            using var target = new SqliteConnection($"Data Source={targetPath};Mode=ReadWriteCreate;Pooling=False");
            target.Open();
            source.BackupDatabase(target);
            Interlocked.Exchange(ref lastBackupUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            PruneBackups(7);
            return targetPath;
        }
        catch (Exception ex)
        {
            RecordError(ex);
            api.Logger.Warning("[ServerEssentials/Storage] Database backup failed: {0}", ex.Message);
            return "";
        }
    }

    private void WriterLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                // RequestSave() signals immediately. A long timeout here is only a safety wakeup
                // for backup maintenance, avoiding an otherwise pointless 1 Hz background wake.
                saveSignal.WaitOne(60_000);
                if (cts.IsCancellationRequested) break;
                if (!dirty) { MaybeBackup(force: false); continue; }
                // Coalesce bursts of moderation/session/inventory events into one transaction.
                Thread.Sleep(750);
                if (cts.IsCancellationRequested) break;
                SaveDirtyState();
                MaybeBackup(force: false);
            }
            catch (Exception ex)
            {
                dirty = true;
                RecordError(ex);
                api.Logger.Error("[ServerEssentials/Storage] Background save failed; state remains dirty for retry: {0}", ex);
                Thread.Sleep(1000);
            }
        }
    }

    private void PersistState(AdminState state)
    {
        var sw = Stopwatch.StartNew();
        lock (flushGate)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            UpsertMeta(conn, tx, state);
            UpsertPlayers(conn, tx, state);
            long warningHigh = UpsertWarnings(conn, tx, state, persistedWarningHighWatermark);
            long noteHigh = UpsertNotes(conn, tx, state, persistedNoteHighWatermark);
            long auditHigh = UpsertAudit(conn, tx, state, persistedAuditHighWatermark);
            long snapshotHigh = UpsertSnapshots(conn, tx, state, persistedSnapshotHighWatermark);
            var offlineTimes = UpsertOfflineInventories(conn, tx, state);
            tx.Commit();
            persistedWarningHighWatermark = Math.Max(persistedWarningHighWatermark, warningHigh);
            persistedNoteHighWatermark = Math.Max(persistedNoteHighWatermark, noteHigh);
            persistedAuditHighWatermark = Math.Max(persistedAuditHighWatermark, auditHigh);
            persistedSnapshotHighWatermark = Math.Max(persistedSnapshotHighWatermark, snapshotHigh);
            foreach (var kv in offlineTimes) persistedOfflineInventoryTimes[kv.Key] = kv.Value;
        }
        sw.Stop();
        Interlocked.Exchange(ref lastSaveUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref lastSaveMilliseconds, sw.ElapsedMilliseconds);
        Interlocked.Increment(ref saveCount);
        // Consumers can release serialized stacks only after the transaction has
        // committed. A failed save leaves all unsaved rollback data available.
        try { InventorySnapshotsCommitted?.Invoke(persistedSnapshotHighWatermark); }
        catch (Exception ex) { api.Logger.Warning("[ServerEssentials/Storage] Could not release persisted snapshot memory: {0}", ex.Message); }
    }

    private void LoadPersistedHighWatermarks()
    {
        try
        {
            using var conn = OpenConnection();
            persistedWarningHighWatermark = ReadMaxId(conn, "warnings");
            persistedNoteHighWatermark = ReadMaxId(conn, "staff_notes");
            persistedAuditHighWatermark = ReadMaxId(conn, "admin_audit");
            persistedSnapshotHighWatermark = ReadMaxId(conn, "inventory_snapshots");
        }
        catch (Exception ex)
        {
            RecordError(ex);
            persistedWarningHighWatermark = persistedNoteHighWatermark = persistedAuditHighWatermark = persistedSnapshotHighWatermark = 0;
        }
    }

    private static long ReadMaxId(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(id),0) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void InitializeAndMigrate()
    {
        int version = 0;
        using (var conn = OpenConnection())
        {
            using var versionCmd = conn.CreateCommand();
            versionCmd.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(versionCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (version > 0 && version < CurrentSchema) CreateBackup("pre-migration");

        using (var conn = OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS players (
    uid TEXT PRIMARY KEY,
    last_name TEXT NOT NULL DEFAULT '',
    first_seen_ms INTEGER NOT NULL DEFAULT 0,
    last_seen_ms INTEGER NOT NULL DEFAULT 0,
    total_play_seconds INTEGER NOT NULL DEFAULT 0,
    session_started_ms INTEGER NOT NULL DEFAULT 0,
    mute_until_ms INTEGER NOT NULL DEFAULT 0,
    permanent_mute INTEGER NOT NULL DEFAULT 0,
    mute_reason TEXT NOT NULL DEFAULT '',
    muted_by TEXT NOT NULL DEFAULT '',
    ban_until_ms INTEGER NOT NULL DEFAULT 0,
    ban_reason TEXT NOT NULL DEFAULT '',
    banned_by TEXT NOT NULL DEFAULT '',
    frozen INTEGER NOT NULL DEFAULT 0,
    freeze_until_ms INTEGER NOT NULL DEFAULT 0,
    frozen_by TEXT NOT NULL DEFAULT '',
    freeze_reason TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_players_name ON players(last_name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_players_last_seen ON players(last_seen_ms DESC);

CREATE TABLE IF NOT EXISTS warnings (
    id INTEGER PRIMARY KEY,
    player_uid TEXT NOT NULL,
    unix_ms INTEGER NOT NULL,
    issued_by TEXT NOT NULL DEFAULT '',
    reason TEXT NOT NULL DEFAULT '',
    removed INTEGER NOT NULL DEFAULT 0,
    removed_by TEXT NOT NULL DEFAULT '',
    removed_ms INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_warnings_player_time ON warnings(player_uid, unix_ms DESC);
CREATE INDEX IF NOT EXISTS idx_warnings_active ON warnings(player_uid, removed, unix_ms DESC);

CREATE TABLE IF NOT EXISTS staff_notes (
    id INTEGER PRIMARY KEY,
    player_uid TEXT NOT NULL,
    unix_ms INTEGER NOT NULL,
    author TEXT NOT NULL DEFAULT '',
    text TEXT NOT NULL DEFAULT '',
    removed INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_notes_player_time ON staff_notes(player_uid, unix_ms DESC);

CREATE TABLE IF NOT EXISTS admin_audit (
    id INTEGER PRIMARY KEY,
    unix_ms INTEGER NOT NULL,
    actor TEXT NOT NULL DEFAULT '',
    action TEXT NOT NULL DEFAULT '',
    target_uid TEXT NOT NULL DEFAULT '',
    target_name TEXT NOT NULL DEFAULT '',
    detail TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT 'ingame'
);
CREATE INDEX IF NOT EXISTS idx_admin_audit_time ON admin_audit(unix_ms DESC);
CREATE INDEX IF NOT EXISTS idx_admin_audit_target_time ON admin_audit(target_uid, unix_ms DESC);
CREATE INDEX IF NOT EXISTS idx_admin_audit_actor_time ON admin_audit(actor, unix_ms DESC);

CREATE TABLE IF NOT EXISTS inventory_snapshots (
    id INTEGER PRIMARY KEY,
    unix_ms INTEGER NOT NULL,
    player_uid TEXT NOT NULL,
    player_name TEXT NOT NULL DEFAULT '',
    created_by TEXT NOT NULL DEFAULT '',
    reason TEXT NOT NULL DEFAULT '',
    occupied_slots INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_inventory_snapshots_player_time ON inventory_snapshots(player_uid, unix_ms DESC);

CREATE TABLE IF NOT EXISTS inventory_snapshot_slots (
    snapshot_id INTEGER NOT NULL,
    inventory_id TEXT NOT NULL,
    slot_index INTEGER NOT NULL,
    item_code TEXT NOT NULL DEFAULT '',
    stack_size INTEGER NOT NULL DEFAULT 0,
    stack_blob BLOB NOT NULL,
    PRIMARY KEY(snapshot_id, inventory_id, slot_index),
    FOREIGN KEY(snapshot_id) REFERENCES inventory_snapshots(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS offline_inventory_views (
    player_uid TEXT PRIMARY KEY,
    captured_ms INTEGER NOT NULL,
    source TEXT NOT NULL,
    slots_json TEXT NOT NULL
);
PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
    }

    private Dictionary<string, long> UpsertOfflineInventories(SqliteConnection conn, SqliteTransaction tx, AdminState state)
    {
        var written = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO offline_inventory_views(player_uid,captured_ms,source,slots_json) VALUES($uid,$ms,$source,$slots)
ON CONFLICT(player_uid) DO UPDATE SET captured_ms=excluded.captured_ms,source=excluded.source,slots_json=excluded.slots_json WHERE excluded.captured_ms > offline_inventory_views.captured_ms;";
        foreach (var kv in state.OfflineInventories)
        {
            var record = kv.Value;
            if (persistedOfflineInventoryTimes.TryGetValue(kv.Key, out long saved) && saved >= record.CapturedUnixMilliseconds) continue;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$uid", kv.Key);
            cmd.Parameters.AddWithValue("$ms", record.CapturedUnixMilliseconds);
            cmd.Parameters.AddWithValue("$source", record.Source);
            // Keep display metadata independent of serialized rollback stack blobs.
            cmd.Parameters.AddWithValue("$slots", System.Text.Json.JsonSerializer.Serialize(record.Slots));
            cmd.ExecuteNonQuery();
            written[kv.Key] = record.CapturedUnixMilliseconds;
        }
        return written;
    }

    private void LoadOfflineInventories(SqliteConnection conn, AdminState state)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT player_uid,captured_ms,source,slots_json FROM offline_inventory_views WHERE length(slots_json) <= 16777216;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var slots = System.Text.Json.JsonSerializer.Deserialize<List<AdminInventorySlotView>>(reader.GetString(3));
                if (slots == null || slots.Count > SavedPlayerInventoryData.MaxSlots) continue;
                string uid = reader.GetString(0);
                long captured = reader.GetInt64(1);
                state.OfflineInventories[uid] = new AdminOfflineInventoryRecord { CapturedUnixMilliseconds = captured, Source = reader.GetString(2), Slots = slots };
                persistedOfflineInventoryTimes[uid] = captured;
            }
            catch (Exception ex) { api.Logger.Warning("[ServerEssentials/Admin] Could not load an offline inventory capture: {0}", ex.Message); }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE; PRAGMA cache_size=-16384; PRAGMA mmap_size=0; PRAGMA wal_autocheckpoint=1000; PRAGMA journal_size_limit=67108864;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void UpsertMeta(SqliteConnection conn, SqliteTransaction tx, AdminState state)
    {
        SetMeta(conn, "maintenance_mode", state.MaintenanceMode ? "1" : "0", tx);
        SetMeta(conn, "maintenance_reason", state.MaintenanceReason ?? "Maintenance", tx);
        SetMeta(conn, "maintenance_until_ms", state.MaintenanceUntilUnixMilliseconds.ToString(CultureInfo.InvariantCulture), tx);
        SetMeta(conn, "admin_state_schema", state.SchemaVersion.ToString(CultureInfo.InvariantCulture), tx);
        SetMeta(conn, "last_persist_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), tx);
    }

    private static void UpsertPlayers(SqliteConnection conn, SqliteTransaction tx, AdminState state)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO players(uid,last_name,first_seen_ms,last_seen_ms,total_play_seconds,session_started_ms,mute_until_ms,permanent_mute,mute_reason,muted_by,ban_until_ms,ban_reason,banned_by,frozen,freeze_until_ms,frozen_by,freeze_reason)
VALUES($uid,$name,$first,$last,$play,$session,$muteUntil,$permMute,$muteReason,$mutedBy,$banUntil,$banReason,$bannedBy,$frozen,$freezeUntil,$frozenBy,$freezeReason)
ON CONFLICT(uid) DO UPDATE SET last_name=excluded.last_name,first_seen_ms=excluded.first_seen_ms,last_seen_ms=excluded.last_seen_ms,total_play_seconds=excluded.total_play_seconds,session_started_ms=excluded.session_started_ms,mute_until_ms=excluded.mute_until_ms,permanent_mute=excluded.permanent_mute,mute_reason=excluded.mute_reason,muted_by=excluded.muted_by,ban_until_ms=excluded.ban_until_ms,ban_reason=excluded.ban_reason,banned_by=excluded.banned_by,frozen=excluded.frozen,freeze_until_ms=excluded.freeze_until_ms,frozen_by=excluded.frozen_by,freeze_reason=excluded.freeze_reason;";
        foreach (var rec in state.Players.Values)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$uid", rec.PlayerUid ?? ""); cmd.Parameters.AddWithValue("$name", rec.LastKnownName ?? "");
            cmd.Parameters.AddWithValue("$first", rec.FirstSeenUnixMilliseconds); cmd.Parameters.AddWithValue("$last", rec.LastSeenUnixMilliseconds);
            cmd.Parameters.AddWithValue("$play", rec.TotalPlaySeconds); cmd.Parameters.AddWithValue("$session", rec.SessionStartedUnixMilliseconds);
            cmd.Parameters.AddWithValue("$muteUntil", rec.MuteUntilUnixMilliseconds); cmd.Parameters.AddWithValue("$permMute", rec.PermanentMute ? 1 : 0);
            cmd.Parameters.AddWithValue("$muteReason", rec.MuteReason ?? ""); cmd.Parameters.AddWithValue("$mutedBy", rec.MutedBy ?? "");
            cmd.Parameters.AddWithValue("$banUntil", rec.BanUntilUnixMilliseconds); cmd.Parameters.AddWithValue("$banReason", rec.BanReason ?? ""); cmd.Parameters.AddWithValue("$bannedBy", rec.BannedBy ?? "");
            cmd.Parameters.AddWithValue("$frozen", rec.Frozen ? 1 : 0); cmd.Parameters.AddWithValue("$freezeUntil", rec.FreezeUntilUnixMilliseconds);
            cmd.Parameters.AddWithValue("$frozenBy", rec.FrozenBy ?? ""); cmd.Parameters.AddWithValue("$freezeReason", rec.FreezeReason ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    private static long UpsertWarnings(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
    {
        long high = persistedHigh;
        using var insert = conn.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = @"INSERT INTO warnings(id,player_uid,unix_ms,issued_by,reason,removed,removed_by,removed_ms) VALUES($id,$uid,$ms,$by,$reason,$removed,$removedBy,$removedMs)
ON CONFLICT(id) DO UPDATE SET player_uid=excluded.player_uid,unix_ms=excluded.unix_ms,issued_by=excluded.issued_by,reason=excluded.reason,removed=excluded.removed,removed_by=excluded.removed_by,removed_ms=excluded.removed_ms;";
        using var markRemoved = conn.CreateCommand(); markRemoved.Transaction = tx;
        markRemoved.CommandText = "UPDATE warnings SET removed=1,removed_by=$removedBy,removed_ms=$removedMs WHERE id=$id AND (removed=0 OR removed_by<>$removedBy OR removed_ms<>$removedMs);";
        foreach (var rec in state.Players.Values) foreach (var row in rec.Warnings)
        {
            if (row.Id > high) high = row.Id;
            if (row.Id > persistedHigh)
            {
                insert.Parameters.Clear(); insert.Parameters.AddWithValue("$id", row.Id); insert.Parameters.AddWithValue("$uid", rec.PlayerUid ?? ""); insert.Parameters.AddWithValue("$ms", row.UnixMilliseconds);
                insert.Parameters.AddWithValue("$by", row.IssuedBy ?? ""); insert.Parameters.AddWithValue("$reason", row.Reason ?? ""); insert.Parameters.AddWithValue("$removed", row.Removed ? 1 : 0);
                insert.Parameters.AddWithValue("$removedBy", row.RemovedBy ?? ""); insert.Parameters.AddWithValue("$removedMs", row.RemovedUnixMilliseconds); insert.ExecuteNonQuery();
            }
            else if (row.Removed)
            {
                markRemoved.Parameters.Clear(); markRemoved.Parameters.AddWithValue("$id", row.Id); markRemoved.Parameters.AddWithValue("$removedBy", row.RemovedBy ?? ""); markRemoved.Parameters.AddWithValue("$removedMs", row.RemovedUnixMilliseconds); markRemoved.ExecuteNonQuery();
            }
        }
        return high;
    }

    private static long UpsertNotes(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
    {
        long high = persistedHigh;
        using var insert = conn.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = @"INSERT INTO staff_notes(id,player_uid,unix_ms,author,text,removed) VALUES($id,$uid,$ms,$author,$text,$removed)
ON CONFLICT(id) DO UPDATE SET player_uid=excluded.player_uid,unix_ms=excluded.unix_ms,author=excluded.author,text=excluded.text,removed=excluded.removed;";
        using var markRemoved = conn.CreateCommand(); markRemoved.Transaction = tx;
        markRemoved.CommandText = "UPDATE staff_notes SET removed=1 WHERE id=$id AND removed=0;";
        foreach (var rec in state.Players.Values) foreach (var row in rec.Notes)
        {
            if (row.Id > high) high = row.Id;
            if (row.Id > persistedHigh)
            {
                insert.Parameters.Clear(); insert.Parameters.AddWithValue("$id", row.Id); insert.Parameters.AddWithValue("$uid", rec.PlayerUid ?? ""); insert.Parameters.AddWithValue("$ms", row.UnixMilliseconds);
                insert.Parameters.AddWithValue("$author", row.Author ?? ""); insert.Parameters.AddWithValue("$text", row.Text ?? ""); insert.Parameters.AddWithValue("$removed", row.Removed ? 1 : 0); insert.ExecuteNonQuery();
            }
            else if (row.Removed)
            {
                markRemoved.Parameters.Clear(); markRemoved.Parameters.AddWithValue("$id", row.Id); markRemoved.ExecuteNonQuery();
            }
        }
        return high;
    }

    private static long UpsertAudit(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
    {
        long high = persistedHigh;
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO admin_audit(id,unix_ms,actor,action,target_uid,target_name,detail,source) VALUES($id,$ms,$actor,$action,$uid,$name,$detail,$source)
ON CONFLICT(id) DO NOTHING;";
        foreach (var row in state.Audit)
        {
            if (row.Id > high) high = row.Id;
            if (row.Id <= persistedHigh) continue;
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", row.Id); cmd.Parameters.AddWithValue("$ms", row.UnixMilliseconds); cmd.Parameters.AddWithValue("$actor", row.Actor ?? "");
            cmd.Parameters.AddWithValue("$action", row.Action ?? ""); cmd.Parameters.AddWithValue("$uid", row.TargetUid ?? ""); cmd.Parameters.AddWithValue("$name", row.TargetName ?? "");
            cmd.Parameters.AddWithValue("$detail", row.Detail ?? ""); cmd.Parameters.AddWithValue("$source", row.Source ?? "ingame"); cmd.ExecuteNonQuery();
        }
        using var prune = conn.CreateCommand(); prune.Transaction = tx;
        prune.CommandText = "DELETE FROM admin_audit WHERE id NOT IN (SELECT id FROM admin_audit ORDER BY unix_ms DESC, id DESC LIMIT 5000);";
        prune.ExecuteNonQuery();
        return high;
    }

    private static long UpsertSnapshots(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
    {
        long high = persistedHigh;
        using var head = conn.CreateCommand(); head.Transaction = tx;
        head.CommandText = @"INSERT INTO inventory_snapshots(id,unix_ms,player_uid,player_name,created_by,reason,occupied_slots) VALUES($id,$ms,$uid,$name,$by,$reason,$count)
ON CONFLICT(id) DO NOTHING;";
        using var slot = conn.CreateCommand(); slot.Transaction = tx;
        slot.CommandText = @"INSERT INTO inventory_snapshot_slots(snapshot_id,inventory_id,slot_index,item_code,stack_size,stack_blob) VALUES($sid,$inv,$slot,$code,$size,$blob)
ON CONFLICT(snapshot_id,inventory_id,slot_index) DO NOTHING;";
        foreach (var kv in state.InventorySnapshots)
        {
            bool addedSnapshot = false;
            foreach (var snap in kv.Value)
            {
                if (snap.Id > high) high = snap.Id;
                if (snap.Id <= persistedHigh) continue;
                addedSnapshot = true;
                head.Parameters.Clear(); head.Parameters.AddWithValue("$id", snap.Id); head.Parameters.AddWithValue("$ms", snap.UnixMilliseconds); head.Parameters.AddWithValue("$uid", snap.PlayerUid ?? kv.Key);
                head.Parameters.AddWithValue("$name", snap.PlayerName ?? ""); head.Parameters.AddWithValue("$by", snap.CreatedBy ?? ""); head.Parameters.AddWithValue("$reason", snap.Reason ?? ""); head.Parameters.AddWithValue("$count", snap.OccupiedSlots); head.ExecuteNonQuery();
                foreach (var row in snap.Slots)
                {
                    var bytes = row.StackBytes ?? Array.Empty<byte>();
                    if (bytes.Length == 0 && !string.IsNullOrWhiteSpace(row.StackBase64))
                    {
                        try { bytes = Convert.FromBase64String(row.StackBase64); } catch { bytes = Array.Empty<byte>(); }
                    }
                    if (bytes.Length == 0) continue;
                    slot.Parameters.Clear(); slot.Parameters.AddWithValue("$sid", snap.Id); slot.Parameters.AddWithValue("$inv", row.Inventory ?? ""); slot.Parameters.AddWithValue("$slot", row.Slot);
                    slot.Parameters.AddWithValue("$code", row.ItemCode ?? ""); slot.Parameters.AddWithValue("$size", row.StackSize); slot.Parameters.Add("$blob", SqliteType.Blob).Value = bytes; slot.ExecuteNonQuery();
                }
            }
            // Existing snapshots are immutable. Avoid running a delete/index scan
            // for every historical player on every moderation or session save.
            if (!addedSnapshot) continue;
            using var prune = conn.CreateCommand(); prune.Transaction = tx;
            prune.CommandText = "DELETE FROM inventory_snapshots WHERE player_uid=$uid AND id NOT IN (SELECT id FROM inventory_snapshots WHERE player_uid=$uid ORDER BY unix_ms DESC, id DESC LIMIT 12);";
            prune.Parameters.AddWithValue("$uid", kv.Key); prune.ExecuteNonQuery();
        }
        return high;
    }

    private static void LoadMeta(SqliteConnection conn, AdminState state)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT key,value FROM meta;";
        using var r = cmd.ExecuteReader(); while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        state.MaintenanceMode = map.TryGetValue("maintenance_mode", out var mode) && mode == "1";
        state.MaintenanceReason = map.TryGetValue("maintenance_reason", out var reason) ? reason : "Maintenance";
        if (map.TryGetValue("maintenance_until_ms", out var until) && long.TryParse(until, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUntil)) state.MaintenanceUntilUnixMilliseconds = parsedUntil;
    }

    private static void LoadPlayers(SqliteConnection conn, AdminState state)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT uid,last_name,first_seen_ms,last_seen_ms,total_play_seconds,session_started_ms,mute_until_ms,permanent_mute,mute_reason,muted_by,ban_until_ms,ban_reason,banned_by,frozen,freeze_until_ms,frozen_by,freeze_reason FROM players;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rec = new PlayerAdminRecord { PlayerUid=r.GetString(0), LastKnownName=r.GetString(1), FirstSeenUnixMilliseconds=r.GetInt64(2), LastSeenUnixMilliseconds=r.GetInt64(3), TotalPlaySeconds=r.GetInt64(4), SessionStartedUnixMilliseconds=r.GetInt64(5), MuteUntilUnixMilliseconds=r.GetInt64(6), PermanentMute=r.GetInt64(7)!=0, MuteReason=r.GetString(8), MutedBy=r.GetString(9), BanUntilUnixMilliseconds=r.GetInt64(10), BanReason=r.GetString(11), BannedBy=r.GetString(12), Frozen=r.GetInt64(13)!=0, FreezeUntilUnixMilliseconds=r.GetInt64(14), FrozenBy=r.GetString(15), FreezeReason=r.GetString(16) };
            state.Players[rec.PlayerUid] = rec;
        }
    }

    private static void CloseStaleSessions(SqliteConnection conn, AdminState state)
    {
        long persistedMs = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key='last_persist_ms' LIMIT 1;";
            var raw = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistedMs);
        }
        if (persistedMs <= 0) persistedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var rec in state.Players.Values)
        {
            if (rec.SessionStartedUnixMilliseconds <= 0) continue;
            rec.TotalPlaySeconds += Math.Max(0, (persistedMs - rec.SessionStartedUnixMilliseconds) / 1000);
            rec.SessionStartedUnixMilliseconds = 0;
        }
    }

    private static void LoadWarnings(SqliteConnection conn, AdminState state)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT id,player_uid,unix_ms,issued_by,reason,removed,removed_by,removed_ms FROM warnings ORDER BY unix_ms;";
        using var r = cmd.ExecuteReader(); while (r.Read()) if (state.Players.TryGetValue(r.GetString(1), out var rec)) rec.Warnings.Add(new AdminWarningRecord { Id=r.GetInt64(0), UnixMilliseconds=r.GetInt64(2), IssuedBy=r.GetString(3), Reason=r.GetString(4), Removed=r.GetInt64(5)!=0, RemovedBy=r.GetString(6), RemovedUnixMilliseconds=r.GetInt64(7) });
    }

    private static void LoadNotes(SqliteConnection conn, AdminState state)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT id,player_uid,unix_ms,author,text,removed FROM staff_notes ORDER BY unix_ms;";
        using var r = cmd.ExecuteReader(); while (r.Read()) if (state.Players.TryGetValue(r.GetString(1), out var rec)) rec.Notes.Add(new AdminNoteRecord { Id=r.GetInt64(0), UnixMilliseconds=r.GetInt64(2), Author=r.GetString(3), Text=r.GetString(4), Removed=r.GetInt64(5)!=0 });
    }

    private static void LoadAudit(SqliteConnection conn, AdminState state)
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT id,unix_ms,actor,action,target_uid,target_name,detail,source FROM admin_audit ORDER BY unix_ms DESC,id DESC LIMIT 5000;";
        using var r = cmd.ExecuteReader(); var rows = new List<AdminAuditRecord>(); while (r.Read()) rows.Add(new AdminAuditRecord { Id=r.GetInt64(0), UnixMilliseconds=r.GetInt64(1), Actor=r.GetString(2), Action=r.GetString(3), TargetUid=r.GetString(4), TargetName=r.GetString(5), Detail=r.GetString(6), Source=r.GetString(7) });
        rows.Reverse(); state.Audit = rows;
    }

    private static void LoadSnapshots(SqliteConnection conn, AdminState state)
    {
        // Headers support listing and record IDs. Even the latest 12/player can hold
        // gigabytes of stack attributes on a long-lived server, so leave those on disk.
        const string rankedSnapshots = @"
WITH ranked AS (
    SELECT id,unix_ms,player_uid,player_name,created_by,reason,occupied_slots,
           ROW_NUMBER() OVER (PARTITION BY player_uid ORDER BY unix_ms DESC,id DESC) AS rn
    FROM inventory_snapshots
)
";

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = rankedSnapshots + @"
SELECT id,unix_ms,player_uid,player_name,created_by,reason,occupied_slots
FROM ranked WHERE rn <= 12 ORDER BY unix_ms,id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var snap = new AdminInventorySnapshotRecord
                {
                    Id=r.GetInt64(0), UnixMilliseconds=r.GetInt64(1), PlayerUid=r.GetString(2),
                    PlayerName=r.GetString(3), CreatedBy=r.GetString(4), Reason=r.GetString(5),
                    OccupiedSlots=r.GetInt32(6), SlotsPayloadLoaded=false
                };
                if (!state.InventorySnapshots.TryGetValue(snap.PlayerUid, out var list))
                    state.InventorySnapshots[snap.PlayerUid] = list = new();
                list.Add(snap);
            }
        }

    }

    public bool TryReadInventorySnapshot(AdminInventorySnapshotRecord header, out AdminInventorySnapshotRecord? snapshot, out string error)
    {
        snapshot = null;
        error = "Inventory snapshot could not be read. No inventory was changed.";
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource=dbPath, Mode=SqliteOpenMode.ReadOnly, Cache=SqliteCacheMode.Private,
                Pooling=false, DefaultTimeout=2
            };
            using var conn = new SqliteConnection(builder.ConnectionString);
            conn.Open();
            using var check = conn.CreateCommand();
            check.CommandText = @"SELECT h.occupied_slots,COUNT(s.snapshot_id),COALESCE(SUM(length(s.stack_blob)),0),
COALESCE(MAX(length(s.stack_blob)),0),COALESCE(MIN(length(s.stack_blob)),1)
FROM inventory_snapshots h LEFT JOIN inventory_snapshot_slots s ON s.snapshot_id=h.id
WHERE h.id=$id AND h.player_uid=$uid GROUP BY h.id;";
            check.Parameters.AddWithValue("$id", header.Id);
            check.Parameters.AddWithValue("$uid", header.PlayerUid);
            using (var reader = check.ExecuteReader())
            {
                if (!reader.Read()) { error = "Inventory snapshot is no longer available. No inventory was changed."; return false; }
                long count = reader.GetInt64(1);
                if (reader.GetInt64(0) != count || count > SavedPlayerInventoryData.MaxSlots ||
                    reader.GetInt64(2) > 64L * 1024 * 1024 || reader.GetInt64(3) > SavedPlayerInventoryData.MaxBytes || reader.GetInt64(4) <= 0)
                {
                    error = "Inventory snapshot is incomplete or exceeds the safe read limit. No inventory was changed.";
                    return false;
                }
            }
            var result = new AdminInventorySnapshotRecord
            {
                Id=header.Id, UnixMilliseconds=header.UnixMilliseconds, PlayerUid=header.PlayerUid,
                PlayerName=header.PlayerName, CreatedBy=header.CreatedBy, Reason=header.Reason,
                OccupiedSlots=header.OccupiedSlots
            };
            using var slotCmd = conn.CreateCommand();
            slotCmd.CommandText = @"SELECT inventory_id,slot_index,item_code,stack_size,stack_blob
FROM inventory_snapshot_slots WHERE snapshot_id=$id ORDER BY inventory_id,slot_index;";
            slotCmd.Parameters.AddWithValue("$id", header.Id);
            using var sr = slotCmd.ExecuteReader();
            long bytes = 0;
            while (sr.Read())
            {
                var blob = (byte[])sr[4];
                bytes += blob.LongLength;
                // Recheck while materializing in case a database edited outside the
                // suite changes after the first statement. Never publish a partial view.
                if (blob.Length == 0 || blob.Length > SavedPlayerInventoryData.MaxBytes || bytes > 64L * 1024 * 1024 || result.Slots.Count >= SavedPlayerInventoryData.MaxSlots)
                    return false;
                result.Slots.Add(new AdminInventorySnapshotSlot
                {
                    Inventory=sr.GetString(0), Slot=sr.GetInt32(1), ItemCode=sr.GetString(2),
                    StackSize=sr.GetInt32(3), StackBytes=blob
                });
            }
            if (result.Slots.Count != result.OccupiedSlots) return false;
            snapshot = result;
            error = "";
            return true;
        }
        catch (Exception ex) { RecordError(ex); return false; }
    }

    private void SetMeta(SqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value ?? ""); cmd.ExecuteNonQuery();
    }

    private void MaybeBackup(bool force)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!force && now - Interlocked.Read(ref lastBackupUnixMilliseconds) < 24L * 60 * 60 * 1000) return;
        var newest = Directory.EnumerateFiles(backupsPath, "essentials-*.db").Where(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (!force && newest != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) < TimeSpan.FromHours(24))
        {
            Interlocked.Exchange(ref lastBackupUnixMilliseconds, new DateTimeOffset(File.GetLastWriteTimeUtc(newest), TimeSpan.Zero).ToUnixTimeMilliseconds());
            return;
        }
        CreateBackup("auto");
    }

    private void PruneBackups(int keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(backupsPath, "essentials-*.db").Where(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Max(1, keep))) File.Delete(file);
        }
        catch { }
    }

    private void RecoverCorruptDatabaseIfNeeded()
    {
        if (!File.Exists(dbPath)) return;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;Pooling=False"); conn.Open();
            using var cmd = conn.CreateCommand(); cmd.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidDataException("SQLite quick_check returned: " + result);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/Storage] essentials.db failed startup integrity check: {0}", ex.Message);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(dbPath, Path.Combine(backupsPath, $"essentials-corrupt-{stamp}.db"), overwrite: true);
                TryMove(dbPath + "-wal", Path.Combine(backupsPath, $"essentials-corrupt-{stamp}.db-wal"));
                TryMove(dbPath + "-shm", Path.Combine(backupsPath, $"essentials-corrupt-{stamp}.db-shm"));
                var backup = Directory.EnumerateFiles(backupsPath, "essentials-*.db").Where(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (backup != null)
                {
                    File.Copy(backup, dbPath, overwrite: true);
                    api.Logger.Warning("[ServerEssentials/Storage] Restored essentials.db from backup {0}", Path.GetFileName(backup));
                }
                else api.Logger.Warning("[ServerEssentials/Storage] No healthy essentials backup was available; a new database will be created. Corrupt copy was preserved.");
            }
            catch (Exception recoveryEx)
            {
                api.Logger.Error("[ServerEssentials/Storage] Database recovery failed: {0}", recoveryEx);
            }
        }
    }

    private static void TryMove(string source, string dest)
    {
        try { if (File.Exists(source)) File.Move(source, dest, overwrite: true); } catch { }
    }

    private void RecordError(Exception ex)
    {
        Volatile.Write(ref lastError, ex.GetType().Name + ": " + ex.Message);
        Interlocked.Exchange(ref lastErrorUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Dispose()
    {
        if (disposed) return;
        try { Flush(); } catch { }
        disposed = true;
        cts.Cancel();
        saveSignal.Set();
        try { writerThread.Join(2500); } catch { }
        saveSignal.Dispose(); cts.Dispose();
    }
}
