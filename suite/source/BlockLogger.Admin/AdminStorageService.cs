using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using BlockLogger.Services;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace BlockLogger.Admin;

public sealed class AdminStorageService : IDisposable
{
	private const int CurrentSchema = 1;

	private readonly ICoreServerAPI api;

	private readonly Func<AdminState> snapshotProvider;

	private readonly string dbPath;

	private readonly string backupsPath;

	private readonly AutoResetEvent saveSignal = new AutoResetEvent(initialState: false);

	private readonly CancellationTokenSource cts = new CancellationTokenSource();

	private readonly Thread writerThread;

	private readonly object flushGate = new object();

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

	private readonly Dictionary<string, long> persistedOfflineInventoryTimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	public string DatabasePath => dbPath;

	public long LastSaveUnixMilliseconds => Interlocked.Read(in lastSaveUnixMilliseconds);

	public long LastSaveMilliseconds => Interlocked.Read(in lastSaveMilliseconds);

	public long SaveCount => Interlocked.Read(in saveCount);

	public long LastBackupUnixMilliseconds => Interlocked.Read(in lastBackupUnixMilliseconds);

	public long LastIntegrityUnixMilliseconds => Interlocked.Read(in lastIntegrityUnixMilliseconds);

	public long LastIntegrityMilliseconds => Interlocked.Read(in lastIntegrityMilliseconds);

	public string LastIntegrityResult => Volatile.Read(in lastIntegrityResult) ?? "Not run";

	public string LastError => Volatile.Read(in lastError) ?? "";

	public long LastErrorUnixMilliseconds => Interlocked.Read(in lastErrorUnixMilliseconds);

	public event Action<long>? InventorySnapshotsCommitted;

	public AdminStorageService(ICoreServerAPI api, Func<AdminState> snapshotProvider)
	{
		this.api = api;
		this.snapshotProvider = snapshotProvider;
		string worldRoot = SuiteDataPaths.GetWorldRoot(api);
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

	public AdminStorageHealth GetHealth()
	{
		return new AdminStorageHealth
		{
			Available = true,
			Path = dbPath,
			DatabaseBytes = (File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0),
			WalBytes = (File.Exists(dbPath + "-wal") ? new FileInfo(dbPath + "-wal").Length : 0),
			ShmBytes = (File.Exists(dbPath + "-shm") ? new FileInfo(dbPath + "-shm").Length : 0),
			SaveCount = SaveCount,
			LastSaveUnixMilliseconds = LastSaveUnixMilliseconds,
			LastSaveMilliseconds = LastSaveMilliseconds,
			LastBackupUnixMilliseconds = LastBackupUnixMilliseconds,
			BackupCount = (Directory.Exists(backupsPath) ? Directory.EnumerateFiles(backupsPath, "essentials-*.db").Count((string x) => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)) : 0),
			LastIntegrityUnixMilliseconds = LastIntegrityUnixMilliseconds,
			LastIntegrityMilliseconds = LastIntegrityMilliseconds,
			Integrity = LastIntegrityResult,
			LastError = LastError,
			LastErrorUnixMilliseconds = LastErrorUnixMilliseconds,
			Dirty = dirty
		};
	}

	public bool HasState()
	{
		try
		{
			using SqliteConnection sqliteConnection = OpenConnection();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM players LIMIT 1) OR EXISTS(SELECT 1 FROM admin_audit LIMIT 1) OR EXISTS(SELECT 1 FROM inventory_snapshots LIMIT 1) OR EXISTS(SELECT 1 FROM meta WHERE key='maintenance_mode');";
			return Convert.ToInt64(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
		}
		catch (Exception ex)
		{
			RecordError(ex);
			return false;
		}
	}

	public AdminState LoadState()
	{
		AdminState adminState = new AdminState
		{
			SchemaVersion = 3
		};
		try
		{
			using SqliteConnection conn = OpenConnection();
			LoadMeta(conn, adminState);
			LoadPlayers(conn, adminState);
			CloseStaleSessions(conn, adminState);
			LoadWarnings(conn, adminState);
			LoadNotes(conn, adminState);
			LoadAudit(conn, adminState);
			LoadSnapshots(conn, adminState);
			LoadOfflineInventories(conn, adminState);
		}
		catch (Exception ex)
		{
			RecordError(ex);
			api.Logger.Error("[ServerEssentials/Storage] Failed to load essentials.db: {0}", ex);
		}
		return adminState;
	}

	public void BackupLegacyJson(AdminState state)
	{
		try
		{
			string text = Path.Combine(backupsPath, $"legacy-admin-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
			File.WriteAllText(text, JsonConvert.SerializeObject(state, Formatting.Indented));
			api.Logger.Notification("[ServerEssentials/Storage] Backed up legacy admin JSON to {0}", text);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Storage] Could not back up legacy admin JSON: {0}", ex.Message);
		}
	}

	public void ImportLegacy(AdminState state)
	{
		PersistState(state);
		using SqliteConnection conn = OpenConnection();
		SetMeta(conn, "legacy_admin_imported", "1");
		SetMeta(conn, "legacy_admin_imported_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
		CreateBackup("post-legacy-import");
	}

	public void RequestSave()
	{
		if (!disposed)
		{
			dirty = true;
			saveSignal.Set();
		}
	}

	public void Flush()
	{
		if (disposed || !dirty)
		{
			return;
		}
		try
		{
			SaveDirtyState();
		}
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
			if (!dirty)
			{
				return;
			}
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
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			using SqliteConnection sqliteConnection = OpenConnection();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "PRAGMA quick_check;";
			string text = Convert.ToString(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
			stopwatch.Stop();
			Volatile.Write(ref lastIntegrityResult, text);
			Interlocked.Exchange(ref lastIntegrityUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			Interlocked.Exchange(ref lastIntegrityMilliseconds, stopwatch.ElapsedMilliseconds);
			return text;
		}
		catch (Exception ex)
		{
			stopwatch.Stop();
			RecordError(ex);
			Volatile.Write(ref lastIntegrityResult, "Failed");
			Interlocked.Exchange(ref lastIntegrityUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			Interlocked.Exchange(ref lastIntegrityMilliseconds, stopwatch.ElapsedMilliseconds);
			return "Failed: " + ex.Message;
		}
	}

	public string CreateBackup(string reason = "manual")
	{
		try
		{
			string value = new string((reason ?? "backup").Where((char ch) =>
			{
				bool flag = char.IsLetterOrDigit(ch);
				bool flag2 = flag;
				if (!flag2)
				{
					bool flag3 = ((ch == '-' || ch == '_') ? true : false);
					flag2 = flag3;
				}
				return flag2;
			}).Take(24).ToArray());
			if (string.IsNullOrWhiteSpace(value))
			{
				value = "backup";
			}
			string text = Path.Combine(backupsPath, $"essentials-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{value}.db");
			using SqliteConnection sqliteConnection = OpenConnection();
			try
			{
				using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
				sqliteCommand.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
				sqliteCommand.ExecuteNonQuery();
			}
			catch
			{
			}
			using SqliteConnection sqliteConnection2 = new SqliteConnection("Data Source=" + text + ";Mode=ReadWriteCreate;Pooling=False");
			sqliteConnection2.Open();
			sqliteConnection.BackupDatabase(sqliteConnection2);
			Interlocked.Exchange(ref lastBackupUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			PruneBackups(7);
			return text;
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
				saveSignal.WaitOne(60000);
				if (cts.IsCancellationRequested)
				{
					break;
				}
				if (!dirty)
				{
					MaybeBackup(force: false);
					continue;
				}
				Thread.Sleep(750);
				if (cts.IsCancellationRequested)
				{
					break;
				}
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
		Stopwatch stopwatch = Stopwatch.StartNew();
		lock (flushGate)
		{
			using SqliteConnection sqliteConnection = OpenConnection();
			using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
			UpsertMeta(sqliteConnection, sqliteTransaction, state);
			UpsertPlayers(sqliteConnection, sqliteTransaction, state);
			long val = UpsertWarnings(sqliteConnection, sqliteTransaction, state, persistedWarningHighWatermark);
			long val2 = UpsertNotes(sqliteConnection, sqliteTransaction, state, persistedNoteHighWatermark);
			long val3 = UpsertAudit(sqliteConnection, sqliteTransaction, state, persistedAuditHighWatermark);
			long val4 = UpsertSnapshots(sqliteConnection, sqliteTransaction, state, persistedSnapshotHighWatermark);
			Dictionary<string, long> dictionary = UpsertOfflineInventories(sqliteConnection, sqliteTransaction, state);
			sqliteTransaction.Commit();
			persistedWarningHighWatermark = Math.Max(persistedWarningHighWatermark, val);
			persistedNoteHighWatermark = Math.Max(persistedNoteHighWatermark, val2);
			persistedAuditHighWatermark = Math.Max(persistedAuditHighWatermark, val3);
			persistedSnapshotHighWatermark = Math.Max(persistedSnapshotHighWatermark, val4);
			foreach (KeyValuePair<string, long> item in dictionary)
			{
				persistedOfflineInventoryTimes[item.Key] = item.Value;
			}
		}
		stopwatch.Stop();
		Interlocked.Exchange(ref lastSaveUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		Interlocked.Exchange(ref lastSaveMilliseconds, stopwatch.ElapsedMilliseconds);
		Interlocked.Increment(ref saveCount);
		try
		{
			InventorySnapshotsCommitted?.Invoke(persistedSnapshotHighWatermark);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Storage] Could not release persisted snapshot memory: {0}", ex.Message);
		}
	}

	private void LoadPersistedHighWatermarks()
	{
		try
		{
			using SqliteConnection conn = OpenConnection();
			persistedWarningHighWatermark = ReadMaxId(conn, "warnings");
			persistedNoteHighWatermark = ReadMaxId(conn, "staff_notes");
			persistedAuditHighWatermark = ReadMaxId(conn, "admin_audit");
			persistedSnapshotHighWatermark = ReadMaxId(conn, "inventory_snapshots");
		}
		catch (Exception ex)
		{
			RecordError(ex);
			persistedWarningHighWatermark = (persistedNoteHighWatermark = (persistedAuditHighWatermark = (persistedSnapshotHighWatermark = 0L)));
		}
	}

	private static long ReadMaxId(SqliteConnection conn, string table)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT COALESCE(MAX(id),0) FROM " + table + ";";
		return Convert.ToInt64(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	private void InitializeAndMigrate()
	{
		int num = 0;
		using (SqliteConnection sqliteConnection = OpenConnection())
		{
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "PRAGMA user_version;";
			num = Convert.ToInt32(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
		}
		if (num > 0 && num < 1)
		{
			CreateBackup("pre-migration");
		}
		using SqliteConnection sqliteConnection2 = OpenConnection();
		using SqliteCommand sqliteCommand2 = sqliteConnection2.CreateCommand();
		sqliteCommand2.CommandText = "\nCREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);\nCREATE TABLE IF NOT EXISTS players (\n    uid TEXT PRIMARY KEY,\n    last_name TEXT NOT NULL DEFAULT '',\n    first_seen_ms INTEGER NOT NULL DEFAULT 0,\n    last_seen_ms INTEGER NOT NULL DEFAULT 0,\n    total_play_seconds INTEGER NOT NULL DEFAULT 0,\n    session_started_ms INTEGER NOT NULL DEFAULT 0,\n    mute_until_ms INTEGER NOT NULL DEFAULT 0,\n    permanent_mute INTEGER NOT NULL DEFAULT 0,\n    mute_reason TEXT NOT NULL DEFAULT '',\n    muted_by TEXT NOT NULL DEFAULT '',\n    ban_until_ms INTEGER NOT NULL DEFAULT 0,\n    ban_reason TEXT NOT NULL DEFAULT '',\n    banned_by TEXT NOT NULL DEFAULT '',\n    frozen INTEGER NOT NULL DEFAULT 0,\n    freeze_until_ms INTEGER NOT NULL DEFAULT 0,\n    frozen_by TEXT NOT NULL DEFAULT '',\n    freeze_reason TEXT NOT NULL DEFAULT ''\n);\nCREATE INDEX IF NOT EXISTS idx_players_name ON players(last_name COLLATE NOCASE);\nCREATE INDEX IF NOT EXISTS idx_players_last_seen ON players(last_seen_ms DESC);\n\nCREATE TABLE IF NOT EXISTS warnings (\n    id INTEGER PRIMARY KEY,\n    player_uid TEXT NOT NULL,\n    unix_ms INTEGER NOT NULL,\n    issued_by TEXT NOT NULL DEFAULT '',\n    reason TEXT NOT NULL DEFAULT '',\n    removed INTEGER NOT NULL DEFAULT 0,\n    removed_by TEXT NOT NULL DEFAULT '',\n    removed_ms INTEGER NOT NULL DEFAULT 0\n);\nCREATE INDEX IF NOT EXISTS idx_warnings_player_time ON warnings(player_uid, unix_ms DESC);\nCREATE INDEX IF NOT EXISTS idx_warnings_active ON warnings(player_uid, removed, unix_ms DESC);\n\nCREATE TABLE IF NOT EXISTS staff_notes (\n    id INTEGER PRIMARY KEY,\n    player_uid TEXT NOT NULL,\n    unix_ms INTEGER NOT NULL,\n    author TEXT NOT NULL DEFAULT '',\n    text TEXT NOT NULL DEFAULT '',\n    removed INTEGER NOT NULL DEFAULT 0\n);\nCREATE INDEX IF NOT EXISTS idx_notes_player_time ON staff_notes(player_uid, unix_ms DESC);\n\nCREATE TABLE IF NOT EXISTS admin_audit (\n    id INTEGER PRIMARY KEY,\n    unix_ms INTEGER NOT NULL,\n    actor TEXT NOT NULL DEFAULT '',\n    action TEXT NOT NULL DEFAULT '',\n    target_uid TEXT NOT NULL DEFAULT '',\n    target_name TEXT NOT NULL DEFAULT '',\n    detail TEXT NOT NULL DEFAULT '',\n    source TEXT NOT NULL DEFAULT 'ingame'\n);\nCREATE INDEX IF NOT EXISTS idx_admin_audit_time ON admin_audit(unix_ms DESC);\nCREATE INDEX IF NOT EXISTS idx_admin_audit_target_time ON admin_audit(target_uid, unix_ms DESC);\nCREATE INDEX IF NOT EXISTS idx_admin_audit_actor_time ON admin_audit(actor, unix_ms DESC);\n\nCREATE TABLE IF NOT EXISTS inventory_snapshots (\n    id INTEGER PRIMARY KEY,\n    unix_ms INTEGER NOT NULL,\n    player_uid TEXT NOT NULL,\n    player_name TEXT NOT NULL DEFAULT '',\n    created_by TEXT NOT NULL DEFAULT '',\n    reason TEXT NOT NULL DEFAULT '',\n    occupied_slots INTEGER NOT NULL DEFAULT 0\n);\nCREATE INDEX IF NOT EXISTS idx_inventory_snapshots_player_time ON inventory_snapshots(player_uid, unix_ms DESC);\n\nCREATE TABLE IF NOT EXISTS inventory_snapshot_slots (\n    snapshot_id INTEGER NOT NULL,\n    inventory_id TEXT NOT NULL,\n    slot_index INTEGER NOT NULL,\n    item_code TEXT NOT NULL DEFAULT '',\n    stack_size INTEGER NOT NULL DEFAULT 0,\n    stack_blob BLOB NOT NULL,\n    PRIMARY KEY(snapshot_id, inventory_id, slot_index),\n    FOREIGN KEY(snapshot_id) REFERENCES inventory_snapshots(id) ON DELETE CASCADE\n);\nCREATE TABLE IF NOT EXISTS offline_inventory_views (\n    player_uid TEXT PRIMARY KEY,\n    captured_ms INTEGER NOT NULL,\n    source TEXT NOT NULL,\n    slots_json TEXT NOT NULL\n);\nPRAGMA user_version = 1;";
		sqliteCommand2.ExecuteNonQuery();
	}

	private Dictionary<string, long> UpsertOfflineInventories(SqliteConnection conn, SqliteTransaction tx, AdminState state)
	{
		Dictionary<string, long> dictionary = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO offline_inventory_views(player_uid,captured_ms,source,slots_json) VALUES($uid,$ms,$source,$slots)\nON CONFLICT(player_uid) DO UPDATE SET captured_ms=excluded.captured_ms,source=excluded.source,slots_json=excluded.slots_json WHERE excluded.captured_ms > offline_inventory_views.captured_ms;";
		foreach (KeyValuePair<string, AdminOfflineInventoryRecord> offlineInventory in state.OfflineInventories)
		{
			AdminOfflineInventoryRecord value = offlineInventory.Value;
			if (!persistedOfflineInventoryTimes.TryGetValue(offlineInventory.Key, out var value2) || value2 < value.CapturedUnixMilliseconds)
			{
				sqliteCommand.Parameters.Clear();
				sqliteCommand.Parameters.AddWithValue("$uid", offlineInventory.Key);
				sqliteCommand.Parameters.AddWithValue("$ms", value.CapturedUnixMilliseconds);
				sqliteCommand.Parameters.AddWithValue("$source", value.Source);
				sqliteCommand.Parameters.AddWithValue("$slots", System.Text.Json.JsonSerializer.Serialize(value.Slots));
				sqliteCommand.ExecuteNonQuery();
				dictionary[offlineInventory.Key] = value.CapturedUnixMilliseconds;
			}
		}
		return dictionary;
	}

	private void LoadOfflineInventories(SqliteConnection conn, AdminState state)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT player_uid,captured_ms,source,slots_json FROM offline_inventory_views WHERE length(slots_json) <= 16777216;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			try
			{
				List<AdminInventorySlotView> list = System.Text.Json.JsonSerializer.Deserialize<List<AdminInventorySlotView>>(sqliteDataReader.GetString(3));
				if (list != null && list.Count <= 8192)
				{
					string key = sqliteDataReader.GetString(0);
					long @int = sqliteDataReader.GetInt64(1);
					state.OfflineInventories[key] = new AdminOfflineInventoryRecord
					{
						CapturedUnixMilliseconds = @int,
						Source = sqliteDataReader.GetString(2),
						Slots = list
					};
					persistedOfflineInventoryTimes[key] = @int;
				}
			}
			catch (Exception ex)
			{
				api.Logger.Warning("[ServerEssentials/Admin] Could not load an offline inventory capture: {0}", ex.Message);
			}
		}
	}

	private SqliteConnection OpenConnection()
	{
		SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadWriteCreate;Cache=Private;Pooling=False");
		sqliteConnection.Open();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE; PRAGMA cache_size=-16384; PRAGMA mmap_size=0; PRAGMA wal_autocheckpoint=1000; PRAGMA journal_size_limit=67108864;";
		sqliteCommand.ExecuteNonQuery();
		return sqliteConnection;
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
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO players(uid,last_name,first_seen_ms,last_seen_ms,total_play_seconds,session_started_ms,mute_until_ms,permanent_mute,mute_reason,muted_by,ban_until_ms,ban_reason,banned_by,frozen,freeze_until_ms,frozen_by,freeze_reason)\nVALUES($uid,$name,$first,$last,$play,$session,$muteUntil,$permMute,$muteReason,$mutedBy,$banUntil,$banReason,$bannedBy,$frozen,$freezeUntil,$frozenBy,$freezeReason)\nON CONFLICT(uid) DO UPDATE SET last_name=excluded.last_name,first_seen_ms=excluded.first_seen_ms,last_seen_ms=excluded.last_seen_ms,total_play_seconds=excluded.total_play_seconds,session_started_ms=excluded.session_started_ms,mute_until_ms=excluded.mute_until_ms,permanent_mute=excluded.permanent_mute,mute_reason=excluded.mute_reason,muted_by=excluded.muted_by,ban_until_ms=excluded.ban_until_ms,ban_reason=excluded.ban_reason,banned_by=excluded.banned_by,frozen=excluded.frozen,freeze_until_ms=excluded.freeze_until_ms,frozen_by=excluded.frozen_by,freeze_reason=excluded.freeze_reason;";
		foreach (PlayerAdminRecord value in state.Players.Values)
		{
			sqliteCommand.Parameters.Clear();
			sqliteCommand.Parameters.AddWithValue("$uid", value.PlayerUid ?? "");
			sqliteCommand.Parameters.AddWithValue("$name", value.LastKnownName ?? "");
			sqliteCommand.Parameters.AddWithValue("$first", value.FirstSeenUnixMilliseconds);
			sqliteCommand.Parameters.AddWithValue("$last", value.LastSeenUnixMilliseconds);
			sqliteCommand.Parameters.AddWithValue("$play", value.TotalPlaySeconds);
			sqliteCommand.Parameters.AddWithValue("$session", value.SessionStartedUnixMilliseconds);
			sqliteCommand.Parameters.AddWithValue("$muteUntil", value.MuteUntilUnixMilliseconds);
			sqliteCommand.Parameters.AddWithValue("$permMute", value.PermanentMute ? 1 : 0);
			sqliteCommand.Parameters.AddWithValue("$muteReason", value.MuteReason ?? "");
			sqliteCommand.Parameters.AddWithValue("$mutedBy", value.MutedBy ?? "");
			sqliteCommand.Parameters.AddWithValue("$banUntil", value.BanUntilUnixMilliseconds);
			sqliteCommand.Parameters.AddWithValue("$banReason", value.BanReason ?? "");
			sqliteCommand.Parameters.AddWithValue("$bannedBy", value.BannedBy ?? "");
			sqliteCommand.Parameters.AddWithValue("$frozen", value.Frozen ? 1 : 0);
			sqliteCommand.Parameters.AddWithValue("$freezeUntil", value.FreezeUntilUnixMilliseconds);
			sqliteCommand.Parameters.AddWithValue("$frozenBy", value.FrozenBy ?? "");
			sqliteCommand.Parameters.AddWithValue("$freezeReason", value.FreezeReason ?? "");
			sqliteCommand.ExecuteNonQuery();
		}
	}

	private static long UpsertWarnings(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
	{
		long num = persistedHigh;
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO warnings(id,player_uid,unix_ms,issued_by,reason,removed,removed_by,removed_ms) VALUES($id,$uid,$ms,$by,$reason,$removed,$removedBy,$removedMs)\nON CONFLICT(id) DO UPDATE SET player_uid=excluded.player_uid,unix_ms=excluded.unix_ms,issued_by=excluded.issued_by,reason=excluded.reason,removed=excluded.removed,removed_by=excluded.removed_by,removed_ms=excluded.removed_ms;";
		using SqliteCommand sqliteCommand2 = conn.CreateCommand();
		sqliteCommand2.Transaction = tx;
		sqliteCommand2.CommandText = "UPDATE warnings SET removed=1,removed_by=$removedBy,removed_ms=$removedMs WHERE id=$id AND (removed=0 OR removed_by<>$removedBy OR removed_ms<>$removedMs);";
		foreach (PlayerAdminRecord value in state.Players.Values)
		{
			foreach (AdminWarningRecord warning in value.Warnings)
			{
				if (warning.Id > num)
				{
					num = warning.Id;
				}
				if (warning.Id > persistedHigh)
				{
					sqliteCommand.Parameters.Clear();
					sqliteCommand.Parameters.AddWithValue("$id", warning.Id);
					sqliteCommand.Parameters.AddWithValue("$uid", value.PlayerUid ?? "");
					sqliteCommand.Parameters.AddWithValue("$ms", warning.UnixMilliseconds);
					sqliteCommand.Parameters.AddWithValue("$by", warning.IssuedBy ?? "");
					sqliteCommand.Parameters.AddWithValue("$reason", warning.Reason ?? "");
					sqliteCommand.Parameters.AddWithValue("$removed", warning.Removed ? 1 : 0);
					sqliteCommand.Parameters.AddWithValue("$removedBy", warning.RemovedBy ?? "");
					sqliteCommand.Parameters.AddWithValue("$removedMs", warning.RemovedUnixMilliseconds);
					sqliteCommand.ExecuteNonQuery();
				}
				else if (warning.Removed)
				{
					sqliteCommand2.Parameters.Clear();
					sqliteCommand2.Parameters.AddWithValue("$id", warning.Id);
					sqliteCommand2.Parameters.AddWithValue("$removedBy", warning.RemovedBy ?? "");
					sqliteCommand2.Parameters.AddWithValue("$removedMs", warning.RemovedUnixMilliseconds);
					sqliteCommand2.ExecuteNonQuery();
				}
			}
		}
		return num;
	}

	private static long UpsertNotes(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
	{
		long num = persistedHigh;
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO staff_notes(id,player_uid,unix_ms,author,text,removed) VALUES($id,$uid,$ms,$author,$text,$removed)\nON CONFLICT(id) DO UPDATE SET player_uid=excluded.player_uid,unix_ms=excluded.unix_ms,author=excluded.author,text=excluded.text,removed=excluded.removed;";
		using SqliteCommand sqliteCommand2 = conn.CreateCommand();
		sqliteCommand2.Transaction = tx;
		sqliteCommand2.CommandText = "UPDATE staff_notes SET removed=1 WHERE id=$id AND removed=0;";
		foreach (PlayerAdminRecord value in state.Players.Values)
		{
			foreach (AdminNoteRecord note in value.Notes)
			{
				if (note.Id > num)
				{
					num = note.Id;
				}
				if (note.Id > persistedHigh)
				{
					sqliteCommand.Parameters.Clear();
					sqliteCommand.Parameters.AddWithValue("$id", note.Id);
					sqliteCommand.Parameters.AddWithValue("$uid", value.PlayerUid ?? "");
					sqliteCommand.Parameters.AddWithValue("$ms", note.UnixMilliseconds);
					sqliteCommand.Parameters.AddWithValue("$author", note.Author ?? "");
					sqliteCommand.Parameters.AddWithValue("$text", note.Text ?? "");
					sqliteCommand.Parameters.AddWithValue("$removed", note.Removed ? 1 : 0);
					sqliteCommand.ExecuteNonQuery();
				}
				else if (note.Removed)
				{
					sqliteCommand2.Parameters.Clear();
					sqliteCommand2.Parameters.AddWithValue("$id", note.Id);
					sqliteCommand2.ExecuteNonQuery();
				}
			}
		}
		return num;
	}

	private static long UpsertAudit(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
	{
		long num = persistedHigh;
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO admin_audit(id,unix_ms,actor,action,target_uid,target_name,detail,source) VALUES($id,$ms,$actor,$action,$uid,$name,$detail,$source)\nON CONFLICT(id) DO NOTHING;";
		foreach (AdminAuditRecord item in state.Audit)
		{
			if (item.Id > num)
			{
				num = item.Id;
			}
			if (item.Id > persistedHigh)
			{
				sqliteCommand.Parameters.Clear();
				sqliteCommand.Parameters.AddWithValue("$id", item.Id);
				sqliteCommand.Parameters.AddWithValue("$ms", item.UnixMilliseconds);
				sqliteCommand.Parameters.AddWithValue("$actor", item.Actor ?? "");
				sqliteCommand.Parameters.AddWithValue("$action", item.Action ?? "");
				sqliteCommand.Parameters.AddWithValue("$uid", item.TargetUid ?? "");
				sqliteCommand.Parameters.AddWithValue("$name", item.TargetName ?? "");
				sqliteCommand.Parameters.AddWithValue("$detail", item.Detail ?? "");
				sqliteCommand.Parameters.AddWithValue("$source", item.Source ?? "ingame");
				sqliteCommand.ExecuteNonQuery();
			}
		}
		using SqliteCommand sqliteCommand2 = conn.CreateCommand();
		sqliteCommand2.Transaction = tx;
		sqliteCommand2.CommandText = "DELETE FROM admin_audit WHERE id NOT IN (SELECT id FROM admin_audit ORDER BY unix_ms DESC, id DESC LIMIT 5000);";
		sqliteCommand2.ExecuteNonQuery();
		return num;
	}

	private static long UpsertSnapshots(SqliteConnection conn, SqliteTransaction tx, AdminState state, long persistedHigh)
	{
		long num = persistedHigh;
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO inventory_snapshots(id,unix_ms,player_uid,player_name,created_by,reason,occupied_slots) VALUES($id,$ms,$uid,$name,$by,$reason,$count)\nON CONFLICT(id) DO NOTHING;";
		using SqliteCommand sqliteCommand2 = conn.CreateCommand();
		sqliteCommand2.Transaction = tx;
		sqliteCommand2.CommandText = "INSERT INTO inventory_snapshot_slots(snapshot_id,inventory_id,slot_index,item_code,stack_size,stack_blob) VALUES($sid,$inv,$slot,$code,$size,$blob)\nON CONFLICT(snapshot_id,inventory_id,slot_index) DO NOTHING;";
		foreach (KeyValuePair<string, List<AdminInventorySnapshotRecord>> inventorySnapshot in state.InventorySnapshots)
		{
			bool flag = false;
			foreach (AdminInventorySnapshotRecord item in inventorySnapshot.Value)
			{
				if (item.Id > num)
				{
					num = item.Id;
				}
				if (item.Id <= persistedHigh)
				{
					continue;
				}
				flag = true;
				sqliteCommand.Parameters.Clear();
				sqliteCommand.Parameters.AddWithValue("$id", item.Id);
				sqliteCommand.Parameters.AddWithValue("$ms", item.UnixMilliseconds);
				sqliteCommand.Parameters.AddWithValue("$uid", item.PlayerUid ?? inventorySnapshot.Key);
				sqliteCommand.Parameters.AddWithValue("$name", item.PlayerName ?? "");
				sqliteCommand.Parameters.AddWithValue("$by", item.CreatedBy ?? "");
				sqliteCommand.Parameters.AddWithValue("$reason", item.Reason ?? "");
				sqliteCommand.Parameters.AddWithValue("$count", item.OccupiedSlots);
				sqliteCommand.ExecuteNonQuery();
				foreach (AdminInventorySnapshotSlot slot in item.Slots)
				{
					byte[] array = slot.StackBytes ?? Array.Empty<byte>();
					if (array.Length == 0 && !string.IsNullOrWhiteSpace(slot.StackBase64))
					{
						try
						{
							array = Convert.FromBase64String(slot.StackBase64);
						}
						catch
						{
							array = Array.Empty<byte>();
						}
					}
					if (array.Length != 0)
					{
						sqliteCommand2.Parameters.Clear();
						sqliteCommand2.Parameters.AddWithValue("$sid", item.Id);
						sqliteCommand2.Parameters.AddWithValue("$inv", slot.Inventory ?? "");
						sqliteCommand2.Parameters.AddWithValue("$slot", slot.Slot);
						sqliteCommand2.Parameters.AddWithValue("$code", slot.ItemCode ?? "");
						sqliteCommand2.Parameters.AddWithValue("$size", slot.StackSize);
						sqliteCommand2.Parameters.Add("$blob", SqliteType.Blob).Value = array;
						sqliteCommand2.ExecuteNonQuery();
					}
				}
			}
			if (flag)
			{
				using SqliteCommand sqliteCommand3 = conn.CreateCommand();
				sqliteCommand3.Transaction = tx;
				sqliteCommand3.CommandText = "DELETE FROM inventory_snapshots WHERE player_uid=$uid AND id NOT IN (SELECT id FROM inventory_snapshots WHERE player_uid=$uid ORDER BY unix_ms DESC, id DESC LIMIT 12);";
				sqliteCommand3.Parameters.AddWithValue("$uid", inventorySnapshot.Key);
				sqliteCommand3.ExecuteNonQuery();
			}
		}
		return num;
	}

	private static void LoadMeta(SqliteConnection conn, AdminState state)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT key,value FROM meta;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			dictionary[sqliteDataReader.GetString(0)] = sqliteDataReader.GetString(1);
		}
		state.MaintenanceMode = dictionary.TryGetValue("maintenance_mode", out var value) && value == "1";
		state.MaintenanceReason = (dictionary.TryGetValue("maintenance_reason", out var value2) ? value2 : "Maintenance");
		if (dictionary.TryGetValue("maintenance_until_ms", out var value3) && long.TryParse(value3, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			state.MaintenanceUntilUnixMilliseconds = result;
		}
	}

	private static void LoadPlayers(SqliteConnection conn, AdminState state)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT uid,last_name,first_seen_ms,last_seen_ms,total_play_seconds,session_started_ms,mute_until_ms,permanent_mute,mute_reason,muted_by,ban_until_ms,ban_reason,banned_by,frozen,freeze_until_ms,frozen_by,freeze_reason FROM players;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			PlayerAdminRecord playerAdminRecord = new PlayerAdminRecord
			{
				PlayerUid = sqliteDataReader.GetString(0),
				LastKnownName = sqliteDataReader.GetString(1),
				FirstSeenUnixMilliseconds = sqliteDataReader.GetInt64(2),
				LastSeenUnixMilliseconds = sqliteDataReader.GetInt64(3),
				TotalPlaySeconds = sqliteDataReader.GetInt64(4),
				SessionStartedUnixMilliseconds = sqliteDataReader.GetInt64(5),
				MuteUntilUnixMilliseconds = sqliteDataReader.GetInt64(6),
				PermanentMute = (sqliteDataReader.GetInt64(7) != 0),
				MuteReason = sqliteDataReader.GetString(8),
				MutedBy = sqliteDataReader.GetString(9),
				BanUntilUnixMilliseconds = sqliteDataReader.GetInt64(10),
				BanReason = sqliteDataReader.GetString(11),
				BannedBy = sqliteDataReader.GetString(12),
				Frozen = (sqliteDataReader.GetInt64(13) != 0),
				FreezeUntilUnixMilliseconds = sqliteDataReader.GetInt64(14),
				FrozenBy = sqliteDataReader.GetString(15),
				FreezeReason = sqliteDataReader.GetString(16)
			};
			state.Players[playerAdminRecord.PlayerUid] = playerAdminRecord;
		}
	}

	private static void CloseStaleSessions(SqliteConnection conn, AdminState state)
	{
		long result = 0L;
		using (SqliteCommand sqliteCommand = conn.CreateCommand())
		{
			sqliteCommand.CommandText = "SELECT value FROM meta WHERE key='last_persist_ms' LIMIT 1;";
			string s = Convert.ToString(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
			long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
		}
		if (result <= 0)
		{
			result = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		foreach (PlayerAdminRecord value in state.Players.Values)
		{
			if (value.SessionStartedUnixMilliseconds > 0)
			{
				value.TotalPlaySeconds += Math.Max(0L, (result - value.SessionStartedUnixMilliseconds) / 1000);
				value.SessionStartedUnixMilliseconds = 0L;
			}
		}
	}

	private static void LoadWarnings(SqliteConnection conn, AdminState state)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT id,player_uid,unix_ms,issued_by,reason,removed,removed_by,removed_ms FROM warnings ORDER BY unix_ms;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			if (state.Players.TryGetValue(sqliteDataReader.GetString(1), out PlayerAdminRecord value))
			{
				value.Warnings.Add(new AdminWarningRecord
				{
					Id = sqliteDataReader.GetInt64(0),
					UnixMilliseconds = sqliteDataReader.GetInt64(2),
					IssuedBy = sqliteDataReader.GetString(3),
					Reason = sqliteDataReader.GetString(4),
					Removed = (sqliteDataReader.GetInt64(5) != 0),
					RemovedBy = sqliteDataReader.GetString(6),
					RemovedUnixMilliseconds = sqliteDataReader.GetInt64(7)
				});
			}
		}
	}

	private static void LoadNotes(SqliteConnection conn, AdminState state)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT id,player_uid,unix_ms,author,text,removed FROM staff_notes ORDER BY unix_ms;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			if (state.Players.TryGetValue(sqliteDataReader.GetString(1), out PlayerAdminRecord value))
			{
				value.Notes.Add(new AdminNoteRecord
				{
					Id = sqliteDataReader.GetInt64(0),
					UnixMilliseconds = sqliteDataReader.GetInt64(2),
					Author = sqliteDataReader.GetString(3),
					Text = sqliteDataReader.GetString(4),
					Removed = (sqliteDataReader.GetInt64(5) != 0)
				});
			}
		}
	}

	private static void LoadAudit(SqliteConnection conn, AdminState state)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT id,unix_ms,actor,action,target_uid,target_name,detail,source FROM admin_audit ORDER BY unix_ms DESC,id DESC LIMIT 5000;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		List<AdminAuditRecord> list = new List<AdminAuditRecord>();
		while (sqliteDataReader.Read())
		{
			list.Add(new AdminAuditRecord
			{
				Id = sqliteDataReader.GetInt64(0),
				UnixMilliseconds = sqliteDataReader.GetInt64(1),
				Actor = sqliteDataReader.GetString(2),
				Action = sqliteDataReader.GetString(3),
				TargetUid = sqliteDataReader.GetString(4),
				TargetName = sqliteDataReader.GetString(5),
				Detail = sqliteDataReader.GetString(6),
				Source = sqliteDataReader.GetString(7)
			});
		}
		list.Reverse();
		state.Audit = list;
	}

	private static void LoadSnapshots(SqliteConnection conn, AdminState state)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "\nWITH ranked AS (\n    SELECT id,unix_ms,player_uid,player_name,created_by,reason,occupied_slots,\n           ROW_NUMBER() OVER (PARTITION BY player_uid ORDER BY unix_ms DESC,id DESC) AS rn\n    FROM inventory_snapshots\n)\n\nSELECT id,unix_ms,player_uid,player_name,created_by,reason,occupied_slots\nFROM ranked WHERE rn <= 12 ORDER BY unix_ms,id;";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			AdminInventorySnapshotRecord adminInventorySnapshotRecord = new AdminInventorySnapshotRecord
			{
				Id = sqliteDataReader.GetInt64(0),
				UnixMilliseconds = sqliteDataReader.GetInt64(1),
				PlayerUid = sqliteDataReader.GetString(2),
				PlayerName = sqliteDataReader.GetString(3),
				CreatedBy = sqliteDataReader.GetString(4),
				Reason = sqliteDataReader.GetString(5),
				OccupiedSlots = sqliteDataReader.GetInt32(6),
				SlotsPayloadLoaded = false
			};
			if (!state.InventorySnapshots.TryGetValue(adminInventorySnapshotRecord.PlayerUid, out List<AdminInventorySnapshotRecord> value))
			{
				value = (state.InventorySnapshots[adminInventorySnapshotRecord.PlayerUid] = new List<AdminInventorySnapshotRecord>());
			}
			value.Add(adminInventorySnapshotRecord);
		}
	}

	public bool TryReadInventorySnapshot(AdminInventorySnapshotRecord header, out AdminInventorySnapshotRecord? snapshot, out string error)
	{
		snapshot = null;
		error = "Inventory snapshot could not be read. No inventory was changed.";
		try
		{
			SqliteConnectionStringBuilder sqliteConnectionStringBuilder = new SqliteConnectionStringBuilder
			{
				DataSource = dbPath,
				Mode = SqliteOpenMode.ReadOnly,
				Cache = SqliteCacheMode.Private,
				Pooling = false,
				DefaultTimeout = 2
			};
			using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionStringBuilder.ConnectionString);
			sqliteConnection.Open();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "SELECT h.occupied_slots,COUNT(s.snapshot_id),COALESCE(SUM(length(s.stack_blob)),0),\nCOALESCE(MAX(length(s.stack_blob)),0),COALESCE(MIN(length(s.stack_blob)),1)\nFROM inventory_snapshots h LEFT JOIN inventory_snapshot_slots s ON s.snapshot_id=h.id\nWHERE h.id=$id AND h.player_uid=$uid GROUP BY h.id;";
			sqliteCommand.Parameters.AddWithValue("$id", header.Id);
			sqliteCommand.Parameters.AddWithValue("$uid", header.PlayerUid);
			using (SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader())
			{
				if (!sqliteDataReader.Read())
				{
					error = "Inventory snapshot is no longer available. No inventory was changed.";
					return false;
				}
				long @int = sqliteDataReader.GetInt64(1);
				if (sqliteDataReader.GetInt64(0) != @int || @int > 8192 || sqliteDataReader.GetInt64(2) > 67108864 || sqliteDataReader.GetInt64(3) > 16777216 || sqliteDataReader.GetInt64(4) <= 0)
				{
					error = "Inventory snapshot is incomplete or exceeds the safe read limit. No inventory was changed.";
					return false;
				}
			}
			AdminInventorySnapshotRecord adminInventorySnapshotRecord = new AdminInventorySnapshotRecord
			{
				Id = header.Id,
				UnixMilliseconds = header.UnixMilliseconds,
				PlayerUid = header.PlayerUid,
				PlayerName = header.PlayerName,
				CreatedBy = header.CreatedBy,
				Reason = header.Reason,
				OccupiedSlots = header.OccupiedSlots
			};
			using SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
			sqliteCommand2.CommandText = "SELECT inventory_id,slot_index,item_code,stack_size,stack_blob\nFROM inventory_snapshot_slots WHERE snapshot_id=$id ORDER BY inventory_id,slot_index;";
			sqliteCommand2.Parameters.AddWithValue("$id", header.Id);
			using SqliteDataReader sqliteDataReader2 = sqliteCommand2.ExecuteReader();
			long num = 0L;
			while (sqliteDataReader2.Read())
			{
				byte[] array = (byte[])sqliteDataReader2[4];
				num += array.LongLength;
				if (array.Length == 0 || array.Length > 16777216 || num > 67108864 || adminInventorySnapshotRecord.Slots.Count >= 8192)
				{
					return false;
				}
				adminInventorySnapshotRecord.Slots.Add(new AdminInventorySnapshotSlot
				{
					Inventory = sqliteDataReader2.GetString(0),
					Slot = sqliteDataReader2.GetInt32(1),
					ItemCode = sqliteDataReader2.GetString(2),
					StackSize = sqliteDataReader2.GetInt32(3),
					StackBytes = array
				});
			}
			if (adminInventorySnapshotRecord.Slots.Count != adminInventorySnapshotRecord.OccupiedSlots)
			{
				return false;
			}
			snapshot = adminInventorySnapshotRecord;
			error = "";
			return true;
		}
		catch (Exception ex)
		{
			RecordError(ex);
			return false;
		}
	}

	private void SetMeta(SqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.Transaction = tx;
		sqliteCommand.CommandText = "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
		sqliteCommand.Parameters.AddWithValue("$key", key);
		sqliteCommand.Parameters.AddWithValue("$value", value ?? "");
		sqliteCommand.ExecuteNonQuery();
	}

	private void MaybeBackup(bool force)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (force || num - Interlocked.Read(in lastBackupUnixMilliseconds) >= 86400000)
		{
			string text = (from x in Directory.EnumerateFiles(backupsPath, "essentials-*.db")
				where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
				select x).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
			if (!force && text != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(text) < TimeSpan.FromHours(24))
			{
				Interlocked.Exchange(ref lastBackupUnixMilliseconds, new DateTimeOffset(File.GetLastWriteTimeUtc(text), TimeSpan.Zero).ToUnixTimeMilliseconds());
			}
			else
			{
				CreateBackup("auto");
			}
		}
	}

	private void PruneBackups(int keep)
	{
		try
		{
			foreach (string item in (from x in Directory.EnumerateFiles(backupsPath, "essentials-*.db")
				where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
				select x).OrderByDescending(File.GetLastWriteTimeUtc).Skip(Math.Max(1, keep)))
			{
				File.Delete(item);
			}
		}
		catch
		{
		}
	}

	private void RecoverCorruptDatabaseIfNeeded()
	{
		if (!File.Exists(dbPath))
		{
			return;
		}
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadWrite;Pooling=False");
			sqliteConnection.Open();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "PRAGMA quick_check;";
			string text = Convert.ToString(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "unknown";
			if (string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			throw new InvalidDataException("SQLite quick_check returned: " + text);
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/Storage] essentials.db failed startup integrity check: {0}", ex.Message);
			string text2 = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			try
			{
				File.Move(dbPath, Path.Combine(backupsPath, "essentials-corrupt-" + text2 + ".db"), overwrite: true);
				TryMove(dbPath + "-wal", Path.Combine(backupsPath, "essentials-corrupt-" + text2 + ".db-wal"));
				TryMove(dbPath + "-shm", Path.Combine(backupsPath, "essentials-corrupt-" + text2 + ".db-shm"));
				string text3 = (from x in Directory.EnumerateFiles(backupsPath, "essentials-*.db")
					where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
					select x).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
				if (text3 != null)
				{
					File.Copy(text3, dbPath, overwrite: true);
					api.Logger.Warning("[ServerEssentials/Storage] Restored essentials.db from backup {0}", Path.GetFileName(text3));
				}
				else
				{
					api.Logger.Warning("[ServerEssentials/Storage] No healthy essentials backup was available; a new database will be created. Corrupt copy was preserved.");
				}
			}
			catch (Exception ex2)
			{
				api.Logger.Error("[ServerEssentials/Storage] Database recovery failed: {0}", ex2);
			}
		}
	}

	private static void TryMove(string source, string dest)
	{
		try
		{
			if (File.Exists(source))
			{
				File.Move(source, dest, overwrite: true);
			}
		}
		catch
		{
		}
	}

	private void RecordError(Exception ex)
	{
		Volatile.Write(ref lastError, ex.GetType().Name + ": " + ex.Message);
		Interlocked.Exchange(ref lastErrorUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
	}

	public void Dispose()
	{
		if (!disposed)
		{
			try
			{
				Flush();
			}
			catch
			{
			}
			disposed = true;
			cts.Cancel();
			saveSignal.Set();
			try
			{
				writerThread.Join(2500);
			}
			catch
			{
			}
			saveSignal.Dispose();
			cts.Dispose();
		}
	}
}
