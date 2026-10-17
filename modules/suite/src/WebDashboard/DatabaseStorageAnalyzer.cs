using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace BlockLogger.WebDashboard;

internal static class DatabaseStorageAnalyzer
{
    private sealed class ObjectStat
    {
        public long Bytes { get; set; }
        public long LeafCells { get; set; }
    }

    private sealed class SchemaObject
    {
        public string Type { get; init; } = "";
        public string Name { get; init; } = "";
        public string TableName { get; init; } = "";
    }

    public static DatabaseStorageDatabaseSnapshot Analyze(
        string key,
        string name,
        string path,
        int backupCount,
        long backupBytes,
        int retentionDays) => Analyze(key,name,path,backupCount,backupBytes,retentionDays,true);

    internal static DatabaseStorageDatabaseSnapshot Analyze(string key,string name,string path,int backupCount,long backupBytes,int retentionDays,bool allowSystemInspection)
    {
        var result = CreateLightweight(key, name, path, backupCount, backupBytes, retentionDays);
        if (!result.Available) return result;

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=3000;";
                pragma.ExecuteNonQuery();
            }

            result.PageSizeBytes = ReadPragmaLong(conn, "page_size");
            result.PageCount = ReadPragmaLong(conn, "page_count");
            result.FreePageCount = ReadPragmaLong(conn, "freelist_count");
            result.LogicalDatabaseBytes = SafeMultiply(result.PageSizeBytes, result.PageCount);
            if (result.LogicalDatabaseBytes <= 0) result.LogicalDatabaseBytes = result.DatabaseBytes;
            result.FreeBytes = SafeMultiply(result.PageSizeBytes, result.FreePageCount);

            var schema = ReadSchema(conn);
            var analysisClock=Stopwatch.StartNew();
            long deadline=12000;
            SQLitePCL.raw.sqlite3_progress_handler(conn.Handle!,1000,_=>analysisClock.ElapsedMilliseconds>=deadline?1:0,null!);
            var stats = ReadDbStatsBounded(conn, out string dbStatError);
            if(stats==null&&allowSystemInspection&&analysisClock.ElapsedMilliseconds<12000){
                var native=NativeStorageInspector.TryRead(path,out var nativeMessage);
                if(native!=null)stats=native.ToDictionary(x=>x.Key,x=>new ObjectStat {Bytes=x.Value.Bytes,LeafCells=x.Value.Cells},StringComparer.OrdinalIgnoreCase);
                dbStatError=nativeMessage;
            }
            result.DbStatAvailable = stats != null;
            result.Detailed = stats != null;

            if (stats == null)
            {
                result.PayloadEstimates=true;
                analysisClock.Restart();
                foreach(var table in schema.Where(x=>x.Type=="table"&&!x.Name.StartsWith("sqlite_",StringComparison.OrdinalIgnoreCase)).OrderBy(x=>x.Name)) {
                    var guidance=Guidance(key,table.Name,retentionDays);
                    var row=new DatabaseStorageTableSnapshot {Name=table.Name,DisplayName=guidance.DisplayName,Subsystem=guidance.Subsystem,StoredFields=guidance.StoredFields,Description=guidance.Description,GrowthDriver=guidance.GrowthDriver,Improvement=guidance.Improvement,Safety=guidance.Safety,SizeEstimated=true};
                    result.Tables.Add(row);
                    if(analysisClock.ElapsedMilliseconds>=5000)continue;
                    string Quote(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
                    var columns=new List<string>();
                    deadline=Math.Min(5000,analysisClock.ElapsedMilliseconds+500);
                    try { using var info=conn.CreateCommand();info.CommandText="PRAGMA table_info("+Quote(table.Name)+");";using var reader=info.ExecuteReader();while(reader.Read())columns.Add(reader.GetString(1)); }catch{continue;}
                    if(columns.Count==0)continue;
                    try { using var count=conn.CreateCommand();count.CommandText="SELECT COUNT(*) FROM "+Quote(table.Name);row.ApproxRows=Convert.ToInt64(count.ExecuteScalar());row.RowCountKnown=true; }catch{}
                    deadline=Math.Min(5000,analysisClock.ElapsedMilliseconds+500);
                    try {
                        using var sample=conn.CreateCommand();
                        string expression=string.Join("+",columns.Select(c=>"COALESCE(length(CAST("+Quote(c)+" AS BLOB)),0)"));
                        sample.CommandText="SELECT COUNT(*),COALESCE(AVG(payload),0) FROM (SELECT "+expression+" AS payload FROM "+Quote(table.Name)+" LIMIT 256)";
                        using var reader=sample.ExecuteReader();if(reader.Read()){row.SampledRows=reader.GetInt32(0);row.AveragePayloadBytes=reader.GetDouble(1);if(row.RowCountKnown)row.EstimatedPayloadBytes=(long)Math.Min(long.MaxValue,row.AveragePayloadBytes*row.ApproxRows);}
                    }catch{}
                }
                result.Tables=result.Tables.OrderByDescending(x=>x.EstimatedPayloadBytes).ThenBy(x=>x.Name).ToList();
                foreach(var content in result.Contents)content.Present=result.Tables.Any(x=>x.Name.Equals(content.TableName,StringComparison.OrdinalIgnoreCase));
                result.AnalysisMessage="Exact page ownership is unavailable. Bounded estimates use up to 256 first records per table; distributions may differ elsewhere. Payload estimates exclude indexes, page overhead and free space and are not a breakdown of the whole file. Unknown counts are shown explicitly. File/WAL/backup/free-page totals remain measured. "+dbStatError;
                AddDatabaseRecommendations(result);
                return result;
            }

            var indexBytesByTable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in schema.Where(x => x.Type.Equals("index", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(item.TableName) || item.TableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!stats.TryGetValue(item.Name, out var indexStat)) continue;
                indexBytesByTable.TryGetValue(item.TableName, out long current);
                indexBytesByTable[item.TableName] = current + indexStat.Bytes;
            }

            foreach (var table in schema
                         .Where(x => x.Type.Equals("table", StringComparison.OrdinalIgnoreCase))
                         .Where(x => !x.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                stats.TryGetValue(table.Name, out var tableStat);
                indexBytesByTable.TryGetValue(table.Name, out long indexBytes);
                long dataBytes = tableStat?.Bytes ?? 0;
                long totalBytes = Math.Max(0, dataBytes + indexBytes);
                var guidance = Guidance(key, table.Name, retentionDays);
                result.Tables.Add(new DatabaseStorageTableSnapshot
                {
                    Name = table.Name,
                    DisplayName = guidance.DisplayName,
                    Subsystem = guidance.Subsystem,
                    StoredFields = guidance.StoredFields,
                    ApproxRows = Math.Max(0, tableStat?.LeafCells ?? 0),
                    DataBytes = dataBytes,
                    IndexBytes = indexBytes,
                    TotalBytes = totalBytes,
                    PercentOfDatabase = result.LogicalDatabaseBytes <= 0 ? 0 : totalBytes * 100d / result.LogicalDatabaseBytes,
                    Description = guidance.Description,
                    GrowthDriver = guidance.GrowthDriver,
                    Improvement = guidance.Improvement,
                    Safety = guidance.Safety
                });
            }

            UpdateContentDetails(result);
            result.Tables = result.Tables.OrderByDescending(x => x.TotalBytes).ThenBy(x => x.Name).ToList();
            result.TableDataBytes = result.Tables.Sum(x => x.DataBytes);
            result.IndexBytes = result.Tables.Sum(x => x.IndexBytes);
            long attributed = result.TableDataBytes + result.IndexBytes + result.FreeBytes;
            result.SQLiteOverheadBytes = Math.Max(0, result.LogicalDatabaseBytes - attributed);
            result.AnalysisMessage = "Detailed SQLite page analysis completed. Table totals include each table's indexes; free pages are allocated inside the DB file but currently unused.";
            AddDatabaseRecommendations(result);
            return result;
        }
        catch (Exception ex)
        {
            result.AnalysisMessage = "Storage analysis failed without changing the database: " + ex.Message;
            result.Detailed = false;
            AddDatabaseRecommendations(result);
            return result;
        }
    }

    public static DatabaseStorageDatabaseSnapshot CreateLightweight(
        string key,
        string name,
        string path,
        int backupCount,
        long backupBytes,
        int retentionDays)
    {
        var result = new DatabaseStorageDatabaseSnapshot
        {
            Key = key,
            Name = name,
            FileName = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path),
            Available = !string.IsNullOrWhiteSpace(path) && File.Exists(path),
            Detailed = false,
            DatabaseBytes = FileSize(path),
            WalBytes = FileSize(path + "-wal"),
            ShmBytes = FileSize(path + "-shm"),
            BackupBytes = Math.Max(0, backupBytes),
            BackupCount = Math.Max(0, backupCount),
            RetentionDays = Math.Max(0, retentionDays),
            AnalysisMessage = "Press Analyze Storage to calculate per-table and index page usage. This scan is manual so large databases are not repeatedly walked by dashboard polling."
        };
        result.Contents = BuildKnownContents(key, result.Available);
        return result;
    }

    public static long GetBackupBytes(string databasePath, string prefix)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(databasePath)) return 0;
            string? root = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(root)) return 0;
            string backups = Path.Combine(root, "backups");
            if (!Directory.Exists(backups)) return 0;
            return Directory.EnumerateFiles(backups, prefix + "-*.db")
                .Where(x => !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                .Sum(FileSize);
        }
        catch
        {
            return 0;
        }
    }

    private static List<SchemaObject> ReadSchema(SqliteConnection conn)
    {
        var list = new List<SchemaObject>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name FROM sqlite_master WHERE type IN ('table','index');";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SchemaObject
            {
                Type = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                TableName = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }
        return list;
    }

    private static Dictionary<string,ObjectStat>? ReadDbStatsBounded(SqliteConnection conn,out string error)
    {
        object gate=new();bool active=true;var handle=conn.Handle!;
        using var timer=new System.Threading.Timer(_=>{lock(gate){if(active)SQLitePCL.raw.sqlite3_interrupt(handle);}},null,12000,System.Threading.Timeout.Infinite);
        try{return ReadDbStats(conn,out error);}finally{lock(gate)active=false;}
    }

    private static Dictionary<string, ObjectStat>? ReadDbStats(SqliteConnection conn, out string error)
    {
        error = "";
        try
        {
            var result = new Dictionary<string, ObjectStat>(StringComparer.OrdinalIgnoreCase);
            // SQLite 3.31+ exposes dbstat aggregate mode. It returns one row per B-tree
            // instead of one row per page, which avoids materializing a huge result set for
            // multi-GB databases while still giving exact B-tree page bytes.
            try
            {
                using var aggregate = conn.CreateCommand();
                aggregate.CommandText = "SELECT name, pgsize, ncell FROM dbstat WHERE aggregate=TRUE;";
                using var aggregateReader = aggregate.ExecuteReader();
                while (aggregateReader.Read())
                {
                    string name = aggregateReader.IsDBNull(0) ? "" : aggregateReader.GetString(0);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    result[name] = new ObjectStat
                    {
                        Bytes = aggregateReader.IsDBNull(1) ? 0 : aggregateReader.GetInt64(1),
                        // Aggregate dbstat ncell includes a small number of interior B-tree cells,
                        // so this is intentionally exposed as an approximate record count.
                        LeafCells = aggregateReader.IsDBNull(2) ? 0 : aggregateReader.GetInt64(2)
                    };
                }
                return result;
            }
            catch
            {
                result.Clear();
            }

            // Compatibility fallback for SQLite builds without aggregate dbstat mode.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT name,
COALESCE(SUM(pgsize),0) AS bytes,
COALESCE(SUM(CASE WHEN pagetype='leaf' THEN ncell ELSE 0 END),0) AS leaf_cells
FROM dbstat
GROUP BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[name] = new ObjectStat
                {
                    Bytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    LeafCells = reader.IsDBNull(2) ? 0 : reader.GetInt64(2)
                };
            }
            return result;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static long ReadPragmaLong(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA " + name + ";";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private static long FileSize(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static long SafeMultiply(long a, long b)
    {
        if (a <= 0 || b <= 0) return 0;
        try { return checked(a * b); }
        catch { return long.MaxValue; }
    }

    private static void AddDatabaseRecommendations(DatabaseStorageDatabaseSnapshot db)
    {
        if (!db.Available)
        {
            db.Recommendations.Add(new DatabaseStorageRecommendation
            {
                Severity = "Info", Database = db.Name, Area = "Database",
                Finding = "Database file is not currently available.",
                Improvement = "No storage action is required while this subsystem/database is disabled.", Safety = "Safe"
            });
            return;
        }

        if (db.WalBytes >= 128L * 1024 * 1024 || (db.DatabaseBytes > 0 && db.WalBytes >= db.DatabaseBytes / 3))
        {
            db.Recommendations.Add(new DatabaseStorageRecommendation
            {
                Severity = "Watch", Database = db.Name, Area = "WAL",
                Finding = "The SQLite WAL file is relatively large compared with the live database.",
                Improvement = "Let normal checkpoints/backup maintenance complete and look for long-running readers if it stays large. Never delete an active -wal file by hand.", Safety = "Safe"
            });
        }

        if (db.FreeBytes >= 64L * 1024 * 1024 && db.DatabaseBytes > 0 && db.FreeBytes >= db.DatabaseBytes / 5)
        {
            db.Recommendations.Add(new DatabaseStorageRecommendation
            {
                Severity = "Info", Database = db.Name, Area = "Free pages",
                Finding = "A meaningful part of the DB file is already free internally after deletes/retention.",
                Improvement = "No urgent action is needed. VACUUM can physically shrink the file after a major purge, but run it only during a quiet maintenance window because it is I/O-heavy and needs temporary disk space.", Safety = "Maintenance window"
            });
        }

        if (db.BackupBytes > Math.Max(512L * 1024 * 1024, db.DatabaseBytes * 2))
        {
            db.Recommendations.Add(new DatabaseStorageRecommendation
            {
                Severity = "Watch", Database = db.Name, Area = "Backups",
                Finding = "Retained backups are using substantially more disk than the live database.",
                Improvement = "If disk space is tight, reduce backup retention after confirming you still have a recent known-good backup. Do not remove the only recovery copy.", Safety = "Review first"
            });
        }

        var largest = db.Tables.OrderByDescending(x => x.TotalBytes).FirstOrDefault();
        if (largest != null && largest.PercentOfDatabase >= 50)
        {
            db.Recommendations.Add(new DatabaseStorageRecommendation
            {
                Severity = "Info", Database = db.Name, Area = largest.DisplayName,
                Finding = $"{largest.DisplayName} accounts for {largest.PercentOfDatabase:0.0}% of the live DB file.",
                Improvement = largest.Improvement, Safety = largest.Safety
            });
        }
    }

    private static List<DatabaseStorageContentSnapshot> BuildKnownContents(string databaseKey, bool databaseAvailable)
    {
        string[] tables = databaseKey.Equals("blocklogger", StringComparison.OrdinalIgnoreCase)
            ? new[] { "events", "snapshots", "entity_activity", "meta" }
            : new[] { "players", "warnings", "staff_notes", "admin_audit", "inventory_snapshots", "inventory_snapshot_slots", "meta" };

        var result = new List<DatabaseStorageContentSnapshot>(tables.Length);
        foreach (string table in tables)
        {
            var guidance = Guidance(databaseKey, table, 0);
            result.Add(new DatabaseStorageContentSnapshot
            {
                Subsystem = guidance.Subsystem,
                DisplayName = guidance.DisplayName,
                TableName = table,
                Description = guidance.Description,
                StoredFields = guidance.StoredFields,
                Present = databaseAvailable,
                Analyzed = false
            });
        }
        return result;
    }

    private static void UpdateContentDetails(DatabaseStorageDatabaseSnapshot database)
    {
        var tableLookup = database.Tables.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var content in database.Contents)
        {
            if (!tableLookup.TryGetValue(content.TableName, out var table))
            {
                content.Present = false;
                content.Analyzed = true;
                continue;
            }

            content.Present = true;
            content.Analyzed = true;
            content.ApproxRows = table.ApproxRows;
            content.DataBytes = table.DataBytes;
            content.IndexBytes = table.IndexBytes;
            content.TotalBytes = table.TotalBytes;
        }
    }

    private static (string DisplayName, string Subsystem, string Description, string StoredFields, string GrowthDriver, string Improvement, string Safety) Guidance(string databaseKey, string table, int retentionDays)
    {
        string t = table.ToLowerInvariant();
        if (databaseKey.Equals("blocklogger", StringComparison.OrdinalIgnoreCase))
        {
            return t switch
            {
                "events" => ("Interaction Events", "BlockLogger", "Block, container, player-attributed entity and other audit events used by BlockLogger history searches.", "time, action, player UID/name, world + dimension, XYZ, block/entity code, metadata", "Grows with player/world interaction volume and enabled logging categories.", $"Current retention is {retentionDays} day(s). Lower retention if you do not need that much history, disable noisy categories you do not use, or use the BlockLogger maintenance purge. VACUUM only after a large purge if physical disk reclamation is worth the maintenance cost.", "Review policy"),
                "snapshots" => ("Container Snapshots", "Container Logger", "Serialized container/inventory snapshots used to reconstruct container state around logged changes.", "time, position, block/container code, player, inventory ID, changed slot, item totals, serialized slot contents", "Grows with snapshot-enabled container activity; JSON slot payloads can be much larger than simple event rows.", "Keep full container snapshots or per-slot snapshotting disabled unless you need forensic reconstruction, increase debounce if appropriate, and rely on retention/purge for old data.", "Review policy"),
                "entity_activity" => ("Animal / Entity Activity", "Animal / Entity Logger", "Aggregated animal and entity spawn, death, despawn and activity windows used by diagnostics and history analysis.", "time window, action, entity type/code/class, event count, world + dimension, sample XYZ, region", "Grows with entity churn and the aggregation window/policy.", "Use entity aggregation/adaptive aggregation on busy servers and keep historical retention only as long as it is useful for investigations.", "Review policy"),
                "meta" => ("BlockLogger Metadata", "BlockLogger Internal", "Small schema/runtime metadata for the BlockLogger database.", "schema/runtime key-value state", "Normally nearly static.", "Leave this table alone. Its size should be negligible and manual deletion can damage database state.", "Do not delete"),
                _ => (table, "BlockLogger", "Suite-managed BlockLogger data.", "feature-specific audit data", "Depends on the owning BlockLogger feature.", "Use Suite retention/configuration controls rather than deleting SQLite rows manually.", "Review first")
            };
        }

        return t switch
        {
            "players" => ("Player Records", "Player Administration", "Known-player identity, last-seen/playtime and active moderation state.", "UID, last name, first/last seen, playtime, mute/ban/freeze state and reasons", "Mostly grows with the number of unique players who have joined.", "Normally keep this data; one row per player is small. If it ever becomes material, add an explicit stale-player archival policy rather than editing SQLite manually.", "Keep"),
            "warnings" => ("Warnings", "Moderation", "Player warning history, including removed/resolved warning records.", "player UID, time, issuer, reason, removed state and removal audit", "Grows when staff issue warnings.", "Usually worth retaining for moderation context. If it becomes large, use a future resolved-warning retention/archive policy rather than deleting rows by hand.", "Keep / archive"),
            "staff_notes" => ("Staff Notes", "Moderation", "Administrative notes attached to player records.", "player UID, time, author, note text, removed state", "Grows with staff-authored notes.", "Remove obsolete notes through Player Administration when appropriate; otherwise retain them because they are intentional moderation context.", "Review content"),
            "admin_audit" => ("Administration Audit", "Operations / Audit", "Audited in-game and web administration actions.", "time, actor, action, target UID/name, detail, source", "Grows with every supported administrative action.", "Keep enough history for accountability. If this becomes a major share of essentials.db, introduce a time-based audit archive/retention policy instead of raw SQL deletion.", "Keep / archive"),
            "inventory_snapshots" => ("Inventory Snapshot Headers", "Inventory Administration", "Metadata for safety snapshots created before inventory administration changes and manual snapshots.", "time, player, creator, reason, occupied-slot count", "Grows with inventory administration, but old snapshots are capped per player.", "The Suite already caps retained snapshots per player. Avoid unnecessary manual snapshots; the automatic pre-change snapshots are valuable rollback protection.", "Keep"),
            "inventory_snapshot_slots" => ("Inventory Snapshot Contents", "Inventory Administration", "Serialized item-stack BLOBs for each occupied slot in saved inventory snapshots.", "snapshot ID, inventory ID, slot index, item code, stack size, serialized item-stack blob", "Can dominate essentials.db because complete item stacks are stored for rollback fidelity.", "Avoid excessive manual snapshots. The Suite automatically prunes old per-player snapshots; if this area is still dominant, a lower configurable per-player snapshot cap would be the safest future tuning knob.", "Review policy"),
            "meta" => ("Administration Metadata", "Server Essentials Internal", "Small schema and maintenance state values.", "schema/maintenance key-value state", "Nearly static.", "Leave this table alone. It should remain tiny.", "Do not delete"),
            _ => (table, "Server Essentials", "Suite-managed administration data.", "feature-specific administration data", "Depends on the owning administration feature.", "Use Suite controls rather than deleting database rows manually.", "Review first")
        };
    }
}
