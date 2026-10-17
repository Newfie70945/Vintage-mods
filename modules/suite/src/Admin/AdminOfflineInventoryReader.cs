using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using ProtoBuf;

namespace BlockLogger.Admin;

// Subset of Vintage Story 1.22's ServerWorldPlayerData protobuf. Reading only
// field 2 avoids instantiating or initializing saved player entities.
[ProtoContract]
public sealed class SavedPlayerInventoryData
{
    public const int MaxSlots = 8192;
    public const int MaxBytes = 16 * 1024 * 1024;
    [ProtoMember(2)] public Dictionary<string, byte[]>? Inventories { get; set; }
    [ProtoIgnore] public long ReadUnixMilliseconds { get; set; }
    [ProtoIgnore] public string? Error { get; set; }
}

internal sealed class AdminOfflineInventoryReader
{
    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private sealed record CacheEntry(long Created, string Uid, Lazy<SavedPlayerInventoryData> Data);

    public SavedPlayerInventoryData Read(string worldFilepath, string uid)
    {
        string key = worldFilepath + "\n" + uid;
        CacheEntry entry;
        long now = Environment.TickCount64;
        lock (gate)
        {
            if (!cache.TryGetValue(key, out entry!) || now - entry.Created >= 30_000)
            {
                if (cache.Count >= 64) cache.Remove(cache.OrderBy(x => x.Value.Created).First().Key);
                entry = new CacheEntry(now, uid, new Lazy<SavedPlayerInventoryData>(() => ReadWorldSave(worldFilepath, uid), LazyThreadSafetyMode.ExecutionAndPublication));
                cache[key] = entry;
            }
        }
        var result = entry.Data.Value;
        lock (gate)
        {
            long bytes = cache.Values.Where(x => x.Data.IsValueCreated).Sum(x => x.Data.Value.Inventories?.Values.Sum(v => (long)v.Length) ?? 0);
            while (bytes > 32L * 1024 * 1024 && cache.Count > 1)
            {
                var oldest = cache.OrderBy(x => x.Value.Created).First();
                bytes -= oldest.Value.Data.IsValueCreated ? oldest.Value.Data.Value.Inventories?.Values.Sum(v => (long)v.Length) ?? 0 : 0;
                cache.Remove(oldest.Key);
            }
        }
        return result;
    }

    public void Invalidate(string uid)
    {
        lock (gate)
            foreach (string key in cache.Where(x => string.Equals(x.Value.Uid, uid, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
                cache.Remove(key);
    }

    internal static SavedPlayerInventoryData ReadWorldSave(string worldFilepath, string uid)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > 256 || !File.Exists(worldFilepath))
                return new SavedPlayerInventoryData { ReadUnixMilliseconds = now, Error = "The player has no readable saved world inventory." };
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = worldFilepath, Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN length(data) <= $max THEN data ELSE NULL END, length(data) FROM playerdata WHERE playeruid = $uid LIMIT 1;";
            command.Parameters.AddWithValue("$uid", uid);
            command.Parameters.AddWithValue("$max", SavedPlayerInventoryData.MaxBytes);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return new SavedPlayerInventoryData { ReadUnixMilliseconds = now, Error = "No inventory has been saved in this world for this player." };
            if (reader.IsDBNull(0)) return new SavedPlayerInventoryData { ReadUnixMilliseconds = now, Error = "The saved player record is empty or exceeds the safe inventory read limit." };
            byte[] bytes = (byte[])reader.GetValue(0);
            using var stream = new MemoryStream(bytes, false);
            var data = Serializer.Deserialize<SavedPlayerInventoryData>(stream);
            data.ReadUnixMilliseconds = now;
            if (data.Inventories == null) data.Error = "The saved player record contains no supported inventory data.";
            else if (data.Inventories.Count > 64 || data.Inventories.Any(x => x.Key.Length > 512 || x.Value == null || x.Value.Length > SavedPlayerInventoryData.MaxBytes))
                return new SavedPlayerInventoryData { ReadUnixMilliseconds = now, Error = "The saved inventory exceeds the safe read limit." };
            return data;
        }
        catch (Exception)
        {
            return new SavedPlayerInventoryData { ReadUnixMilliseconds = now, Error = "Saved inventory could not be read. It may be unavailable, locked, or use an unsupported format." };
        }
    }
}
