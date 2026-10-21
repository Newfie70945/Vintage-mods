using Microsoft.Data.Sqlite;

namespace BlockLogger.Services;

internal static class LoggerDatabaseSchema
{
    internal static void Ensure(SqliteConnection conn)
    {
            using var transaction = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_ticks INTEGER NOT NULL,
    timestamp_utc TEXT NOT NULL,
    ingame_date TEXT,
    action INTEGER NOT NULL,
    player_uid TEXT,
    player_name TEXT,
    world_id TEXT,
    dimension TEXT,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    z INTEGER NOT NULL,
    block_code TEXT,
    meta_json TEXT,
    legacy_key TEXT
);

DROP INDEX IF EXISTS idx_events_pos_time;
CREATE INDEX IF NOT EXISTS idx_events_world_dim_pos_time ON events(world_id, dimension, x, y, z, timestamp_ticks DESC);
CREATE INDEX IF NOT EXISTS idx_events_time ON events(timestamp_ticks DESC);
CREATE INDEX IF NOT EXISTS idx_events_world_time ON events(world_id, timestamp_ticks DESC);
-- Player history only indexes player-attributed events. System/entity telemetry with an
-- empty player UID does not pay the write cost.
DROP INDEX IF EXISTS idx_events_player_time;
CREATE INDEX IF NOT EXISTS idx_events_player_name_time
ON events(world_id, player_name COLLATE NOCASE, timestamp_ticks DESC)
WHERE player_uid <> '';
CREATE INDEX IF NOT EXISTS idx_events_player_uid_time
ON events(world_id, player_uid, timestamp_ticks DESC)
WHERE player_uid <> '';
DROP INDEX IF EXISTS idx_events_action_time;
CREATE UNIQUE INDEX IF NOT EXISTS idx_events_legacy_key ON events(legacy_key);

CREATE TABLE IF NOT EXISTS snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_ticks INTEGER NOT NULL,
    timestamp_utc TEXT NOT NULL,
    ingame_date TEXT,
    world_id TEXT,
    dimension TEXT,
    x INTEGER NOT NULL,
    y INTEGER NOT NULL,
    z INTEGER NOT NULL,
    block_code TEXT,
    player_uid TEXT,
    player_name TEXT,
    inventory_id TEXT,
    reason TEXT,
    changed_slot_id INTEGER,
    slot_count INTEGER,
    non_empty_slot_count INTEGER,
    total_item_count INTEGER,
    slots_json TEXT
);

DROP INDEX IF EXISTS idx_snapshots_pos_time;
CREATE INDEX IF NOT EXISTS idx_snapshots_world_dim_pos_time ON snapshots(world_id, dimension, x, y, z, timestamp_ticks DESC);
CREATE INDEX IF NOT EXISTS idx_snapshots_time ON snapshots(timestamp_ticks DESC);
CREATE INDEX IF NOT EXISTS idx_snapshots_world_time ON snapshots(world_id, timestamp_ticks DESC);
DROP INDEX IF EXISTS idx_snapshots_inventory_time;

CREATE TABLE IF NOT EXISTS entity_activity (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    window_start_ticks INTEGER NOT NULL,
    window_end_ticks INTEGER NOT NULL,
    action INTEGER NOT NULL,
    entity_type_id INTEGER NOT NULL,
    entity_code TEXT NOT NULL,
    entity_class TEXT,
    event_count INTEGER NOT NULL,
    world_id TEXT,
    dimension TEXT,
    sample_x INTEGER NOT NULL,
    sample_y INTEGER NOT NULL,
    sample_z INTEGER NOT NULL,
    region_x INTEGER NOT NULL DEFAULT 0,
    region_z INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_entity_activity_time ON entity_activity(window_end_ticks DESC);
DROP INDEX IF EXISTS idx_entity_activity_code_time;
";
            cmd.ExecuteNonQuery();
            EnsureColumn(conn, transaction, "entity_activity", "region_x", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, transaction, "entity_activity", "region_z", "INTEGER NOT NULL DEFAULT 0");
            using (var regionIndex = conn.CreateCommand())
            {
                regionIndex.Transaction = transaction;
                regionIndex.CommandText = @"
DROP INDEX IF EXISTS idx_entity_activity_region_time;
CREATE INDEX IF NOT EXISTS idx_entity_activity_world_dim_pos_time
ON entity_activity(world_id, dimension, sample_x, sample_y, sample_z, window_end_ticks DESC);";
                regionIndex.ExecuteNonQuery();
            }
            transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection conn, SqliteTransaction transaction, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = conn.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

}
