using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using ProtoBuf;

namespace BlockLogger.Admin;

internal sealed class AdminOfflineInventoryReader
{
	private sealed record CacheEntry(long Created, string Uid, Lazy<SavedPlayerInventoryData> Data);

	private readonly object gate = new object();

	private readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

	public SavedPlayerInventoryData Read(string worldFilepath, string uid)
	{
		string key = worldFilepath + "\n" + uid;
		long tickCount = Environment.TickCount64;
		CacheEntry value;
		lock (gate)
		{
			if (!cache.TryGetValue(key, out value) || tickCount - value.Created >= 30000)
			{
				if (cache.Count >= 64)
				{
					cache.Remove(cache.OrderBy((KeyValuePair<string, CacheEntry> x) => x.Value.Created).First().Key);
				}
				value = new CacheEntry(tickCount, uid, new Lazy<SavedPlayerInventoryData>(() => ReadWorldSave(worldFilepath, uid), LazyThreadSafetyMode.ExecutionAndPublication));
				cache[key] = value;
			}
		}
		SavedPlayerInventoryData value2 = value.Data.Value;
		lock (gate)
		{
			long num = cache.Values.Where((CacheEntry x) => x.Data.IsValueCreated).Sum((CacheEntry x) => (x.Data.Value.Inventories?.Values.Sum(v => (long)v.Length) ?? 0L));
			while (num > 33554432 && cache.Count > 1)
			{
				KeyValuePair<string, CacheEntry> keyValuePair = cache.OrderBy((KeyValuePair<string, CacheEntry> x) => x.Value.Created).First();
				num -= (keyValuePair.Value.Data.IsValueCreated ? ((keyValuePair.Value.Data.Value.Inventories?.Values.Sum(v => (long)v.Length) ?? 0L)) : 0);
				cache.Remove(keyValuePair.Key);
			}
		}
		return value2;
	}

	public void Invalidate(string uid)
	{
		lock (gate)
		{
			string[] array = (from x in cache
				where string.Equals(x.Value.Uid, uid, StringComparison.OrdinalIgnoreCase)
				select x.Key).ToArray();
			foreach (string key in array)
			{
				cache.Remove(key);
			}
		}
	}

	internal static SavedPlayerInventoryData ReadWorldSave(string worldFilepath, string uid)
	{
		long readUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		try
		{
			if (string.IsNullOrWhiteSpace(uid) || uid.Length > 256 || !File.Exists(worldFilepath))
			{
				return new SavedPlayerInventoryData
				{
					ReadUnixMilliseconds = readUnixMilliseconds,
					Error = "The player has no readable saved world inventory."
				};
			}
			SqliteConnectionStringBuilder sqliteConnectionStringBuilder = new SqliteConnectionStringBuilder
			{
				DataSource = worldFilepath,
				Mode = SqliteOpenMode.ReadOnly,
				Cache = SqliteCacheMode.Private,
				Pooling = false,
				DefaultTimeout = 1
			};
			using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionStringBuilder.ConnectionString);
			sqliteConnection.Open();
			using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
			sqliteCommand.CommandText = "SELECT CASE WHEN length(data) <= $max THEN data ELSE NULL END, length(data) FROM playerdata WHERE playeruid = $uid LIMIT 1;";
			sqliteCommand.Parameters.AddWithValue("$uid", uid);
			sqliteCommand.Parameters.AddWithValue("$max", 16777216);
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			if (!sqliteDataReader.Read())
			{
				return new SavedPlayerInventoryData
				{
					ReadUnixMilliseconds = readUnixMilliseconds,
					Error = "No inventory has been saved in this world for this player."
				};
			}
			if (sqliteDataReader.IsDBNull(0))
			{
				return new SavedPlayerInventoryData
				{
					ReadUnixMilliseconds = readUnixMilliseconds,
					Error = "The saved player record is empty or exceeds the safe inventory read limit."
				};
			}
			byte[] buffer = (byte[])sqliteDataReader.GetValue(0);
			using MemoryStream source = new MemoryStream(buffer, writable: false);
			SavedPlayerInventoryData savedPlayerInventoryData = Serializer.Deserialize<SavedPlayerInventoryData>(source);
			savedPlayerInventoryData.ReadUnixMilliseconds = readUnixMilliseconds;
			if (savedPlayerInventoryData.Inventories == null)
			{
				savedPlayerInventoryData.Error = "The saved player record contains no supported inventory data.";
			}
			else if (savedPlayerInventoryData.Inventories.Count > 64 || savedPlayerInventoryData.Inventories.Any((KeyValuePair<string, byte[]> x) => x.Key.Length > 512 || x.Value == null || x.Value.Length > 16777216))
			{
				return new SavedPlayerInventoryData
				{
					ReadUnixMilliseconds = readUnixMilliseconds,
					Error = "The saved inventory exceeds the safe read limit."
				};
			}
			return savedPlayerInventoryData;
		}
		catch (Exception)
		{
			return new SavedPlayerInventoryData
			{
				ReadUnixMilliseconds = readUnixMilliseconds,
				Error = "Saved inventory could not be read. It may be unavailable, locked, or use an unsupported format."
			};
		}
	}
}
