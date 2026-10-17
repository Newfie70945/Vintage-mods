using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using SQLitePCL;

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

	public static DatabaseStorageDatabaseSnapshot Analyze(string key, string name, string path, int backupCount, long backupBytes, int retentionDays)
	{
		return Analyze(key, name, path, backupCount, backupBytes, retentionDays, allowSystemInspection: true);
	}

	internal static DatabaseStorageDatabaseSnapshot Analyze(string key, string name, string path, int backupCount, long backupBytes, int retentionDays, bool allowSystemInspection)
	{
		DatabaseStorageDatabaseSnapshot databaseStorageDatabaseSnapshot = CreateLightweight(key, name, path, backupCount, backupBytes, retentionDays);
		if (!databaseStorageDatabaseSnapshot.Available)
		{
			return databaseStorageDatabaseSnapshot;
		}
		try
		{
			using SqliteConnection sqliteConnection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Cache=Private;Pooling=False");
			sqliteConnection.Open();
			using (SqliteCommand sqliteCommand = sqliteConnection.CreateCommand())
			{
				sqliteCommand.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=3000;";
				sqliteCommand.ExecuteNonQuery();
			}
			databaseStorageDatabaseSnapshot.PageSizeBytes = ReadPragmaLong(sqliteConnection, "page_size");
			databaseStorageDatabaseSnapshot.PageCount = ReadPragmaLong(sqliteConnection, "page_count");
			databaseStorageDatabaseSnapshot.FreePageCount = ReadPragmaLong(sqliteConnection, "freelist_count");
			databaseStorageDatabaseSnapshot.LogicalDatabaseBytes = SafeMultiply(databaseStorageDatabaseSnapshot.PageSizeBytes, databaseStorageDatabaseSnapshot.PageCount);
			if (databaseStorageDatabaseSnapshot.LogicalDatabaseBytes <= 0)
			{
				databaseStorageDatabaseSnapshot.LogicalDatabaseBytes = databaseStorageDatabaseSnapshot.DatabaseBytes;
			}
			databaseStorageDatabaseSnapshot.FreeBytes = SafeMultiply(databaseStorageDatabaseSnapshot.PageSizeBytes, databaseStorageDatabaseSnapshot.FreePageCount);
			List<SchemaObject> source = ReadSchema(sqliteConnection);
			Stopwatch analysisClock = Stopwatch.StartNew();
			long deadline = 12000L;
			raw.sqlite3_progress_handler(sqliteConnection.Handle, 1000, (object _) => (analysisClock.ElapsedMilliseconds >= deadline) ? 1 : 0, null);
			Dictionary<string, ObjectStat> dictionary = ReadDbStatsBounded(sqliteConnection, out string error);
			if (((dictionary == null) & allowSystemInspection) && analysisClock.ElapsedMilliseconds < 12000)
			{
				Dictionary<string, (long, long)> dictionary2 = NativeStorageInspector.TryRead(path, out string message);
				if (dictionary2 != null)
				{
					dictionary = dictionary2.ToDictionary((KeyValuePair<string, (long Bytes, long Cells)> x) => x.Key, (KeyValuePair<string, (long Bytes, long Cells)> x) => new ObjectStat
					{
						Bytes = x.Value.Bytes,
						LeafCells = x.Value.Cells
					}, StringComparer.OrdinalIgnoreCase);
				}
				error = message;
			}
			databaseStorageDatabaseSnapshot.DbStatAvailable = dictionary != null;
			databaseStorageDatabaseSnapshot.Detailed = dictionary != null;
			if (dictionary == null)
			{
				databaseStorageDatabaseSnapshot.PayloadEstimates = true;
				analysisClock.Restart();
				foreach (SchemaObject item in from x in source
					where x.Type == "table" && !x.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
					orderby x.Name
					select x)
				{
					(string, string, string, string, string, string, string) tuple = Guidance(key, item.Name, retentionDays);
					DatabaseStorageTableSnapshot databaseStorageTableSnapshot = new DatabaseStorageTableSnapshot
					{
						Name = item.Name,
						DisplayName = tuple.Item1,
						Subsystem = tuple.Item2,
						StoredFields = tuple.Item4,
						Description = tuple.Item3,
						GrowthDriver = tuple.Item5,
						Improvement = tuple.Item6,
						Safety = tuple.Item7,
						SizeEstimated = true
					};
					databaseStorageDatabaseSnapshot.Tables.Add(databaseStorageTableSnapshot);
					if (analysisClock.ElapsedMilliseconds >= 5000)
					{
						continue;
					}
					List<string> list = new List<string>();
					deadline = Math.Min(5000L, analysisClock.ElapsedMilliseconds + 500);
					try
					{
						using SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
						sqliteCommand2.CommandText = "PRAGMA table_info(" + Quote(item.Name) + ");";
						using SqliteDataReader sqliteDataReader = sqliteCommand2.ExecuteReader();
						while (sqliteDataReader.Read())
						{
							list.Add(sqliteDataReader.GetString(1));
						}
					}
					catch
					{
						continue;
					}
					if (list.Count == 0)
					{
						continue;
					}
					try
					{
						using SqliteCommand sqliteCommand3 = sqliteConnection.CreateCommand();
						sqliteCommand3.CommandText = "SELECT COUNT(*) FROM " + Quote(item.Name);
						databaseStorageTableSnapshot.ApproxRows = Convert.ToInt64(sqliteCommand3.ExecuteScalar());
						databaseStorageTableSnapshot.RowCountKnown = true;
					}
					catch
					{
					}
					deadline = Math.Min(5000L, analysisClock.ElapsedMilliseconds + 500);
					try
					{
						using SqliteCommand sqliteCommand4 = sqliteConnection.CreateCommand();
						string text = string.Join("+", list.Select((string c) => "COALESCE(length(CAST(" + Quote(c) + " AS BLOB)),0)"));
						sqliteCommand4.CommandText = "SELECT COUNT(*),COALESCE(AVG(payload),0) FROM (SELECT " + text + " AS payload FROM " + Quote(item.Name) + " LIMIT 256)";
						using SqliteDataReader sqliteDataReader2 = sqliteCommand4.ExecuteReader();
						if (sqliteDataReader2.Read())
						{
							databaseStorageTableSnapshot.SampledRows = sqliteDataReader2.GetInt32(0);
							databaseStorageTableSnapshot.AveragePayloadBytes = sqliteDataReader2.GetDouble(1);
							if (databaseStorageTableSnapshot.RowCountKnown)
							{
								databaseStorageTableSnapshot.EstimatedPayloadBytes = (long)Math.Min(9.223372036854776E+18, databaseStorageTableSnapshot.AveragePayloadBytes * (double)databaseStorageTableSnapshot.ApproxRows);
							}
						}
					}
					catch
					{
					}
				}
				databaseStorageDatabaseSnapshot.Tables = (from x in databaseStorageDatabaseSnapshot.Tables
					orderby x.EstimatedPayloadBytes descending, x.Name
					select x).ToList();
				foreach (DatabaseStorageContentSnapshot content in databaseStorageDatabaseSnapshot.Contents)
				{
					content.Present = databaseStorageDatabaseSnapshot.Tables.Any((DatabaseStorageTableSnapshot x) => x.Name.Equals(content.TableName, StringComparison.OrdinalIgnoreCase));
				}
				databaseStorageDatabaseSnapshot.AnalysisMessage = "Exact page ownership is unavailable. Bounded estimates use up to 256 first records per table; distributions may differ elsewhere. Payload estimates exclude indexes, page overhead and free space and are not a breakdown of the whole file. Unknown counts are shown explicitly. File/WAL/backup/free-page totals remain measured. " + error;
				AddDatabaseRecommendations(databaseStorageDatabaseSnapshot);
				return databaseStorageDatabaseSnapshot;
			}
			Dictionary<string, long> dictionary3 = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			foreach (SchemaObject item2 in source.Where((SchemaObject x) => x.Type.Equals("index", StringComparison.OrdinalIgnoreCase)))
			{
				if (!string.IsNullOrWhiteSpace(item2.TableName) && !item2.TableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && dictionary.TryGetValue(item2.Name, out var value))
				{
					dictionary3.TryGetValue(item2.TableName, out var value2);
					dictionary3[item2.TableName] = value2 + value.Bytes;
				}
			}
			foreach (SchemaObject item3 in (from x in source
				where x.Type.Equals("table", StringComparison.OrdinalIgnoreCase)
				where !x.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
				select x).OrderBy((SchemaObject x) => x.Name, StringComparer.OrdinalIgnoreCase))
			{
				dictionary.TryGetValue(item3.Name, out var value3);
				dictionary3.TryGetValue(item3.Name, out var value4);
				long num = value3?.Bytes ?? 0;
				long num2 = Math.Max(0L, num + value4);
				(string, string, string, string, string, string, string) tuple2 = Guidance(key, item3.Name, retentionDays);
				databaseStorageDatabaseSnapshot.Tables.Add(new DatabaseStorageTableSnapshot
				{
					Name = item3.Name,
					DisplayName = tuple2.Item1,
					Subsystem = tuple2.Item2,
					StoredFields = tuple2.Item4,
					ApproxRows = Math.Max(0L, value3?.LeafCells ?? 0),
					DataBytes = num,
					IndexBytes = value4,
					TotalBytes = num2,
					PercentOfDatabase = ((databaseStorageDatabaseSnapshot.LogicalDatabaseBytes <= 0) ? 0.0 : ((double)num2 * 100.0 / (double)databaseStorageDatabaseSnapshot.LogicalDatabaseBytes)),
					Description = tuple2.Item3,
					GrowthDriver = tuple2.Item5,
					Improvement = tuple2.Item6,
					Safety = tuple2.Item7
				});
			}
			UpdateContentDetails(databaseStorageDatabaseSnapshot);
			databaseStorageDatabaseSnapshot.Tables = (from x in databaseStorageDatabaseSnapshot.Tables
				orderby x.TotalBytes descending, x.Name
				select x).ToList();
			databaseStorageDatabaseSnapshot.TableDataBytes = databaseStorageDatabaseSnapshot.Tables.Sum((DatabaseStorageTableSnapshot x) => x.DataBytes);
			databaseStorageDatabaseSnapshot.IndexBytes = databaseStorageDatabaseSnapshot.Tables.Sum((DatabaseStorageTableSnapshot x) => x.IndexBytes);
			long num3 = databaseStorageDatabaseSnapshot.TableDataBytes + databaseStorageDatabaseSnapshot.IndexBytes + databaseStorageDatabaseSnapshot.FreeBytes;
			databaseStorageDatabaseSnapshot.SQLiteOverheadBytes = Math.Max(0L, databaseStorageDatabaseSnapshot.LogicalDatabaseBytes - num3);
			databaseStorageDatabaseSnapshot.AnalysisMessage = "Detailed SQLite page analysis completed. Table totals include each table's indexes; free pages are allocated inside the DB file but currently unused.";
			AddDatabaseRecommendations(databaseStorageDatabaseSnapshot);
			return databaseStorageDatabaseSnapshot;
		}
		catch (Exception ex)
		{
			databaseStorageDatabaseSnapshot.AnalysisMessage = "Storage analysis failed without changing the database: " + ex.Message;
			databaseStorageDatabaseSnapshot.Detailed = false;
			AddDatabaseRecommendations(databaseStorageDatabaseSnapshot);
			return databaseStorageDatabaseSnapshot;
		}
		static string Quote(string text2)
		{
			return "\"" + text2.Replace("\"", "\"\"") + "\"";
		}
	}

	public static DatabaseStorageDatabaseSnapshot CreateLightweight(string key, string name, string path, int backupCount, long backupBytes, int retentionDays)
	{
		DatabaseStorageDatabaseSnapshot databaseStorageDatabaseSnapshot = new DatabaseStorageDatabaseSnapshot
		{
			Key = key,
			Name = name,
			FileName = (string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path)),
			Available = (!string.IsNullOrWhiteSpace(path) && File.Exists(path)),
			Detailed = false,
			DatabaseBytes = FileSize(path),
			WalBytes = FileSize(path + "-wal"),
			ShmBytes = FileSize(path + "-shm"),
			BackupBytes = Math.Max(0L, backupBytes),
			BackupCount = Math.Max(0, backupCount),
			RetentionDays = Math.Max(0, retentionDays),
			AnalysisMessage = "Press Analyze Storage to calculate per-table and index page usage. This scan is manual so large databases are not repeatedly walked by dashboard polling."
		};
		databaseStorageDatabaseSnapshot.Contents = BuildKnownContents(key, databaseStorageDatabaseSnapshot.Available);
		return databaseStorageDatabaseSnapshot;
	}

	public static long GetBackupBytes(string databasePath, string prefix)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(databasePath))
			{
				return 0L;
			}
			string directoryName = Path.GetDirectoryName(databasePath);
			if (string.IsNullOrWhiteSpace(directoryName))
			{
				return 0L;
			}
			string path = Path.Combine(directoryName, "backups");
			if (!Directory.Exists(path))
			{
				return 0L;
			}
			return (from x in Directory.EnumerateFiles(path, prefix + "-*.db")
				where !Path.GetFileName(x).Contains("corrupt", StringComparison.OrdinalIgnoreCase)
				select x).Sum((Func<string, long>)FileSize);
		}
		catch
		{
			return 0L;
		}
	}

	private static List<SchemaObject> ReadSchema(SqliteConnection conn)
	{
		List<SchemaObject> list = new List<SchemaObject>();
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "SELECT type, name, tbl_name FROM sqlite_master WHERE type IN ('table','index');";
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		while (sqliteDataReader.Read())
		{
			list.Add(new SchemaObject
			{
				Type = (sqliteDataReader.IsDBNull(0) ? "" : sqliteDataReader.GetString(0)),
				Name = (sqliteDataReader.IsDBNull(1) ? "" : sqliteDataReader.GetString(1)),
				TableName = (sqliteDataReader.IsDBNull(2) ? "" : sqliteDataReader.GetString(2))
			});
		}
		return list;
	}

	private static Dictionary<string, ObjectStat>? ReadDbStatsBounded(SqliteConnection conn, out string error)
	{
		object gate = new object();
		bool active = true;
		sqlite3 handle = conn.Handle;
		using (new Timer((object? _) =>
		{
			lock (gate)
			{
				if (active)
				{
					raw.sqlite3_interrupt(handle);
				}
			}
		}, null, 12000, -1))
		{
			try
			{
				return ReadDbStats(conn, out error);
			}
			finally
			{
				lock (gate)
				{
					active = false;
				}
			}
		}
	}

	private static Dictionary<string, ObjectStat>? ReadDbStats(SqliteConnection conn, out string error)
	{
		error = "";
		try
		{
			Dictionary<string, ObjectStat> dictionary = new Dictionary<string, ObjectStat>(StringComparer.OrdinalIgnoreCase);
			try
			{
				using SqliteCommand sqliteCommand = conn.CreateCommand();
				sqliteCommand.CommandText = "SELECT name, pgsize, ncell FROM dbstat WHERE aggregate=TRUE;";
				using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
				while (sqliteDataReader.Read())
				{
					string text = (sqliteDataReader.IsDBNull(0) ? "" : sqliteDataReader.GetString(0));
					if (!string.IsNullOrWhiteSpace(text))
					{
						dictionary[text] = new ObjectStat
						{
							Bytes = (sqliteDataReader.IsDBNull(1) ? 0 : sqliteDataReader.GetInt64(1)),
							LeafCells = (sqliteDataReader.IsDBNull(2) ? 0 : sqliteDataReader.GetInt64(2))
						};
					}
				}
				return dictionary;
			}
			catch
			{
				dictionary.Clear();
			}
			using SqliteCommand sqliteCommand2 = conn.CreateCommand();
			sqliteCommand2.CommandText = "SELECT name,\nCOALESCE(SUM(pgsize),0) AS bytes,\nCOALESCE(SUM(CASE WHEN pagetype='leaf' THEN ncell ELSE 0 END),0) AS leaf_cells\nFROM dbstat\nGROUP BY name;";
			using SqliteDataReader sqliteDataReader2 = sqliteCommand2.ExecuteReader();
			while (sqliteDataReader2.Read())
			{
				string text2 = (sqliteDataReader2.IsDBNull(0) ? "" : sqliteDataReader2.GetString(0));
				if (!string.IsNullOrWhiteSpace(text2))
				{
					dictionary[text2] = new ObjectStat
					{
						Bytes = (sqliteDataReader2.IsDBNull(1) ? 0 : sqliteDataReader2.GetInt64(1)),
						LeafCells = (sqliteDataReader2.IsDBNull(2) ? 0 : sqliteDataReader2.GetInt64(2))
					};
				}
			}
			return dictionary;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return null;
		}
	}

	private static long ReadPragmaLong(SqliteConnection conn, string name)
	{
		using SqliteCommand sqliteCommand = conn.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA " + name + ";";
		return Convert.ToInt64(sqliteCommand.ExecuteScalar() ?? ((object)0), CultureInfo.InvariantCulture);
	}

	private static long FileSize(string? path)
	{
		try
		{
			return (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) ? new FileInfo(path).Length : 0;
		}
		catch
		{
			return 0L;
		}
	}

	private static long SafeMultiply(long a, long b)
	{
		if (a <= 0 || b <= 0)
		{
			return 0L;
		}
		try
		{
			return checked(a * b);
		}
		catch
		{
			return long.MaxValue;
		}
	}

	private static void AddDatabaseRecommendations(DatabaseStorageDatabaseSnapshot db)
	{
		if (!db.Available)
		{
			db.Recommendations.Add(new DatabaseStorageRecommendation
			{
				Severity = "Info",
				Database = db.Name,
				Area = "Database",
				Finding = "Database file is not currently available.",
				Improvement = "No storage action is required while this subsystem/database is disabled.",
				Safety = "Safe"
			});
			return;
		}
		if (db.WalBytes >= 134217728 || (db.DatabaseBytes > 0 && db.WalBytes >= db.DatabaseBytes / 3))
		{
			db.Recommendations.Add(new DatabaseStorageRecommendation
			{
				Severity = "Watch",
				Database = db.Name,
				Area = "WAL",
				Finding = "The SQLite WAL file is relatively large compared with the live database.",
				Improvement = "Let normal checkpoints/backup maintenance complete and look for long-running readers if it stays large. Never delete an active -wal file by hand.",
				Safety = "Safe"
			});
		}
		if (db.FreeBytes >= 67108864 && db.DatabaseBytes > 0 && db.FreeBytes >= db.DatabaseBytes / 5)
		{
			db.Recommendations.Add(new DatabaseStorageRecommendation
			{
				Severity = "Info",
				Database = db.Name,
				Area = "Free pages",
				Finding = "A meaningful part of the DB file is already free internally after deletes/retention.",
				Improvement = "No urgent action is needed. VACUUM can physically shrink the file after a major purge, but run it only during a quiet maintenance window because it is I/O-heavy and needs temporary disk space.",
				Safety = "Maintenance window"
			});
		}
		if (db.BackupBytes > Math.Max(536870912L, db.DatabaseBytes * 2))
		{
			db.Recommendations.Add(new DatabaseStorageRecommendation
			{
				Severity = "Watch",
				Database = db.Name,
				Area = "Backups",
				Finding = "Retained backups are using substantially more disk than the live database.",
				Improvement = "If disk space is tight, reduce backup retention after confirming you still have a recent known-good backup. Do not remove the only recovery copy.",
				Safety = "Review first"
			});
		}
		DatabaseStorageTableSnapshot databaseStorageTableSnapshot = db.Tables.OrderByDescending((DatabaseStorageTableSnapshot x) => x.TotalBytes).FirstOrDefault();
		if (databaseStorageTableSnapshot != null && databaseStorageTableSnapshot.PercentOfDatabase >= 50.0)
		{
			db.Recommendations.Add(new DatabaseStorageRecommendation
			{
				Severity = "Info",
				Database = db.Name,
				Area = databaseStorageTableSnapshot.DisplayName,
				Finding = $"{databaseStorageTableSnapshot.DisplayName} accounts for {databaseStorageTableSnapshot.PercentOfDatabase:0.0}% of the live DB file.",
				Improvement = databaseStorageTableSnapshot.Improvement,
				Safety = databaseStorageTableSnapshot.Safety
			});
		}
	}

	private static List<DatabaseStorageContentSnapshot> BuildKnownContents(string databaseKey, bool databaseAvailable)
	{
		string[] array = ((!databaseKey.Equals("blocklogger", StringComparison.OrdinalIgnoreCase)) ? new string[7] { "players", "warnings", "staff_notes", "admin_audit", "inventory_snapshots", "inventory_snapshot_slots", "meta" } : new string[4] { "events", "snapshots", "entity_activity", "meta" });
		List<DatabaseStorageContentSnapshot> list = new List<DatabaseStorageContentSnapshot>(array.Length);
		string[] array2 = array;
		foreach (string text in array2)
		{
			(string, string, string, string, string, string, string) tuple = Guidance(databaseKey, text, 0);
			list.Add(new DatabaseStorageContentSnapshot
			{
				Subsystem = tuple.Item2,
				DisplayName = tuple.Item1,
				TableName = text,
				Description = tuple.Item3,
				StoredFields = tuple.Item4,
				Present = databaseAvailable,
				Analyzed = false
			});
		}
		return list;
	}

	private static void UpdateContentDetails(DatabaseStorageDatabaseSnapshot database)
	{
		Dictionary<string, DatabaseStorageTableSnapshot> dictionary = database.Tables.ToDictionary((DatabaseStorageTableSnapshot x) => x.Name, StringComparer.OrdinalIgnoreCase);
		foreach (DatabaseStorageContentSnapshot content in database.Contents)
		{
			if (!dictionary.TryGetValue(content.TableName, out var value))
			{
				content.Present = false;
				content.Analyzed = true;
				continue;
			}
			content.Present = true;
			content.Analyzed = true;
			content.ApproxRows = value.ApproxRows;
			content.DataBytes = value.DataBytes;
			content.IndexBytes = value.IndexBytes;
			content.TotalBytes = value.TotalBytes;
		}
	}

	private static (string DisplayName, string Subsystem, string Description, string StoredFields, string GrowthDriver, string Improvement, string Safety) Guidance(string databaseKey, string table, int retentionDays)
	{
		string text = table.ToLowerInvariant();
		(string, string, string, string, string, string, string) result;
		if (databaseKey.Equals("blocklogger", StringComparison.OrdinalIgnoreCase))
		{
			if (1 == 0)
			{
			}
			result = text switch
			{
				"events" => ("Interaction Events", "BlockLogger", "Block, container, player-attributed entity and other audit events used by BlockLogger history searches.", "time, action, player UID/name, world + dimension, XYZ, block/entity code, metadata", "Grows with player/world interaction volume and enabled logging categories.", $"Current retention is {retentionDays} day(s). Lower retention if you do not need that much history, disable noisy categories you do not use, or use the BlockLogger maintenance purge. VACUUM only after a large purge if physical disk reclamation is worth the maintenance cost.", "Review policy"), 
				"snapshots" => ("Container Snapshots", "Container Logger", "Serialized container/inventory snapshots used to reconstruct container state around logged changes.", "time, position, block/container code, player, inventory ID, changed slot, item totals, serialized slot contents", "Grows with snapshot-enabled container activity; JSON slot payloads can be much larger than simple event rows.", "Keep full container snapshots or per-slot snapshotting disabled unless you need forensic reconstruction, increase debounce if appropriate, and rely on retention/purge for old data.", "Review policy"), 
				"entity_activity" => ("Animal / Entity Activity", "Animal / Entity Logger", "Aggregated animal and entity spawn, death, despawn and activity windows used by diagnostics and history analysis.", "time window, action, entity type/code/class, event count, world + dimension, sample XYZ, region", "Grows with entity churn and the aggregation window/policy.", "Use entity aggregation/adaptive aggregation on busy servers and keep historical retention only as long as it is useful for investigations.", "Review policy"), 
				"meta" => ("BlockLogger Metadata", "BlockLogger Internal", "Small schema/runtime metadata for the BlockLogger database.", "schema/runtime key-value state", "Normally nearly static.", "Leave this table alone. Its size should be negligible and manual deletion can damage database state.", "Do not delete"), 
				_ => (table, "BlockLogger", "Suite-managed BlockLogger data.", "feature-specific audit data", "Depends on the owning BlockLogger feature.", "Use Suite retention/configuration controls rather than deleting SQLite rows manually.", "Review first"), 
			};
			if (1 == 0)
			{
			}
			return result;
		}
		if (1 == 0)
		{
		}
		result = text switch
		{
			"players" => ("Player Records", "Player Administration", "Known-player identity, last-seen/playtime and active moderation state.", "UID, last name, first/last seen, playtime, mute/ban/freeze state and reasons", "Mostly grows with the number of unique players who have joined.", "Normally keep this data; one row per player is small. If it ever becomes material, add an explicit stale-player archival policy rather than editing SQLite manually.", "Keep"), 
			"warnings" => ("Warnings", "Moderation", "Player warning history, including removed/resolved warning records.", "player UID, time, issuer, reason, removed state and removal audit", "Grows when staff issue warnings.", "Usually worth retaining for moderation context. If it becomes large, use a future resolved-warning retention/archive policy rather than deleting rows by hand.", "Keep / archive"), 
			"staff_notes" => ("Staff Notes", "Moderation", "Administrative notes attached to player records.", "player UID, time, author, note text, removed state", "Grows with staff-authored notes.", "Remove obsolete notes through Player Administration when appropriate; otherwise retain them because they are intentional moderation context.", "Review content"), 
			"admin_audit" => ("Administration Audit", "Operations / Audit", "Audited in-game and web administration actions.", "time, actor, action, target UID/name, detail, source", "Grows with every supported administrative action.", "Keep enough history for accountability. If this becomes a major share of essentials.db, introduce a time-based audit archive/retention policy instead of raw SQL deletion.", "Keep / archive"), 
			"inventory_snapshots" => ("Inventory Snapshot Headers", "Inventory Administration", "Metadata for safety snapshots created before inventory administration changes and manual snapshots.", "time, player, creator, reason, occupied-slot count", "Grows with inventory administration, but old snapshots are capped per player.", "The Suite already caps retained snapshots per player. Avoid unnecessary manual snapshots; the automatic pre-change snapshots are valuable rollback protection.", "Keep"), 
			"inventory_snapshot_slots" => ("Inventory Snapshot Contents", "Inventory Administration", "Serialized item-stack BLOBs for each occupied slot in saved inventory snapshots.", "snapshot ID, inventory ID, slot index, item code, stack size, serialized item-stack blob", "Can dominate essentials.db because complete item stacks are stored for rollback fidelity.", "Avoid excessive manual snapshots. The Suite automatically prunes old per-player snapshots; if this area is still dominant, a lower configurable per-player snapshot cap would be the safest future tuning knob.", "Review policy"), 
			"meta" => ("Administration Metadata", "Server Essentials Internal", "Small schema and maintenance state values.", "schema/maintenance key-value state", "Nearly static.", "Leave this table alone. It should remain tiny.", "Do not delete"), 
			_ => (table, "Server Essentials", "Suite-managed administration data.", "feature-specific administration data", "Depends on the owning administration feature.", "Use Suite controls rather than deleting database rows manually.", "Review first"), 
		};
		if (1 == 0)
		{
		}
		return result;
	}
}
