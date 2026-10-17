using System.Security.Cryptography;
using BlockLogger.Admin;
using Microsoft.Data.Sqlite;
using ProtoBuf;

internal static class Program
{
    [ProtoContract] private sealed class FullSavedPlayer
    {
        [ProtoMember(1)] public string Uid { get; set; } = "target";
        [ProtoMember(2)] public Dictionary<string, byte[]> Inventories { get; set; } = new();
        [ProtoMember(3)] public byte[] EntityData { get; set; } = new byte[] { 123, 91, 255 }; // Must not be decoded or initialized.
    }
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    static byte[] Blob(FullSavedPlayer data) { using var ms = new MemoryStream(); Serializer.Serialize(ms, data); return ms.ToArray(); }
    static void Put(string path, string uid, byte[] data)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO playerdata(playeruid,data) VALUES($uid,$data);";
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$data", data); command.ExecuteNonQuery();
    }
    static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "nss-offline-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "world.vcdb");
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            { c.Open(); using var q = c.CreateCommand(); q.CommandText = "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY,playeruid TEXT,data BLOB);"; q.ExecuteNonQuery(); }
            byte[] slotTree = { 1, 2, 3, 4, 5 };
            Put(path, "target", Blob(new FullSavedPlayer { Inventories = new() { ["hotbar-target"] = slotTree } }));
            Put(path, "empty", Blob(new FullSavedPlayer()));
            Put(path, "corrupt", new byte[] { 255, 255, 255 });
            Put(path, "oversize", new byte[SavedPlayerInventoryData.MaxBytes + 1]);
            Put(path, "many", Blob(new FullSavedPlayer { Inventories = Enumerable.Range(0, 65).ToDictionary(x => "inventory-" + x, x => slotTree) }));
            byte[] largeTree = new byte[12 * 1024 * 1024];
            foreach (string uid in new[] { "large-a", "large-b", "large-c" }) Put(path, uid, Blob(new FullSavedPlayer { Inventories = new() { ["hotbar-" + uid] = largeTree } }));
            byte[] before = Hash(path);
            var data = AdminOfflineInventoryReader.ReadWorldSave(path, "target");
            Check(data.Error == null && data.Inventories?.Count == 1 && data.Inventories["hotbar-target"].SequenceEqual(slotTree), "Read only the inventory field from a complete player protobuf.");
            Check(data.ReadUnixMilliseconds > 0, "Track read time without inventing a native capture timestamp.");
            Check(AdminOfflineInventoryReader.ReadWorldSave(path, "missing").Inventories == null, "Missing player must be unavailable, not empty.");
            Check(AdminOfflineInventoryReader.ReadWorldSave(path, "target' OR 1=1 --").Inventories == null, "UID SQL injection must never select another player.");
            Check(AdminOfflineInventoryReader.ReadWorldSave(path, "corrupt").Error != null, "Corrupt save data must fail safely.");
            Check(AdminOfflineInventoryReader.ReadWorldSave(path, "oversize").Error != null, "Oversized blobs must be rejected before materialization.");
            Check(AdminOfflineInventoryReader.ReadWorldSave(path, "many").Error != null, "Too many inventories must be rejected.");
            Check(before.SequenceEqual(Hash(path)), "Every offline read must leave the world database byte-for-byte unchanged.");
            var cache = new AdminOfflineInventoryReader(); var cached = cache.Read(path, "target");
            Check(ReferenceEquals(cached, cache.Read(path, "target")), "Repeated requests must share the bounded cached result.");
            cache.Invalidate("target"); Check(!ReferenceEquals(cached, cache.Read(path, "target")), "Disconnect capture must invalidate a stale saved-data read.");
            var concurrent = Enumerable.Range(0, 32).Select(_ => Task.Run(() => cache.Read(path, "target"))).ToArray();
            Task.WaitAll(concurrent); Check(concurrent.All(x => ReferenceEquals(concurrent[0].Result, x.Result)), "Concurrent identical reads must share one result.");
            var bounded = new AdminOfflineInventoryReader(); var firstLarge = bounded.Read(path, "large-a");
            bounded.Read(path, "large-b"); bounded.Read(path, "large-c");
            Check(!ReferenceEquals(firstLarge, bounded.Read(path, "large-a")), "Aggregate payload retention must evict the oldest record above 32 MiB.");
            Check(before.SequenceEqual(Hash(path)), "Cache pressure and concurrent reads may not modify the database.");
            string missingFile = Path.Combine(root, "missing.vcdb");
            Check(AdminOfflineInventoryReader.ReadWorldSave(missingFile, "target").Error != null && !File.Exists(missingFile), "Reading an absent world may not create one.");
            Console.WriteLine("Offline inventory tests passed: protobuf subset, UID isolation, missing/corrupt/capped data, immutable database, cache and invalidation.");
        }
        finally { Directory.Delete(root, true); }
    }
}
