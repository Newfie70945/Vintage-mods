using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY"));
string modPath = Path.GetFullPath(args.FirstOrDefault() ?? throw new Exception("Pass the compiled Suite DLL"));
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { Path.GetDirectoryName(modPath)!, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string path = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Assembly suite = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
RuntimeHelpers.RunModuleConstructor(suite.ManifestModule.ModuleHandle);
Checks.Run(suite);

static class Checks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static byte[] Serialize<T>(T value) { using var ms = new MemoryStream(); Serializer.Serialize(ms, value); return ms.ToArray(); }
    static T Deserialize<T>(byte[] bytes) { using var ms = new MemoryStream(bytes); return Serializer.Deserialize<T>(ms); }
    static object Proxy(Type type, System.Func<MethodInfo, object?[]?, object?> handler) {
        object proxy = DispatchProxy.Create(type, typeof(ApiProxy)); ((ApiProxy)proxy).Handler = handler; return proxy;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(Assembly suite)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nss-terrain-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try { Test(suite, temporary); } finally { Directory.Delete(temporary, true); }
    }
    static void Test(Assembly suite, string temporary)
    {
        var server = (ServerMain)RuntimeHelpers.GetUninitializedObject(typeof(ServerMain));
        var sourcePool = new ChunkDataPool(32, server);
        var original = ServerChunk.CreateNew(sourcePool);
        original.Unpack();
        original.Data.ClearBlocksAndPrepare();
        for (int y = 0; y < 32; y++) for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) {
            int index = (y * 32 + z) * 32 + x;
            original.Data.SetBlockUnsafe(index, (y < 4 ? 1 : 0));
            if (y == 4) original.Data.SetFluid(index, 2);
        }
        var payload = Deserialize<FullChunk>(original.ToBytes());
        original.Unpack_ReadOnly();
        int[] expected = Enumerable.Range(0, 32768).Select(i => original.Data.GetBlockId(i, BlockLayersAccess.Default)).ToArray();
        int[] expectedFluid = Enumerable.Range(0, 32768).Select(i => original.Data.GetBlockId(i, BlockLayersAccess.Fluid)).ToArray();
        payload.EntitiesCount = 1; payload.Entities = new() { new byte[] { 255, 255, 255 } };
        payload.BlockEntitiesCount = 1; payload.BlockEntities = new() { new byte[] { 255, 255, 255 } };
        payload.Decors = new byte[] { 255, 255, 255 };
        payload.ModData = new() { ["must-not-hydrate"] = new byte[2 * 1024 * 1024] };
        payload.ServerModData = new() { ["must-not-hydrate-server"] = new byte[2 * 1024 * 1024] };
        byte[] bytes = Serialize(payload);
        string database = Path.Combine(temporary, "fixture.vcdbs");
        using (var db = new SqliteConnection("Data Source=" + database)) {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY,data BLOB); CREATE TABLE chunk(position INTEGER PRIMARY KEY,data BLOB);";
            command.ExecuteNonQuery();
            void Row(string table, long position, byte[] value) {
                command.CommandText = "INSERT INTO " + table + " VALUES($p,$d)";
                command.Parameters.Clear(); command.Parameters.AddWithValue("$p", position); command.Parameters.AddWithValue("$d", value); command.ExecuteNonQuery();
            }
            Row("mapchunk", unchecked((long)ChunkPos.ToChunkIndex(0, 0, 0)), Serialize(new MapPayload { Heights = Enumerable.Repeat((ushort)35, 1024).ToArray() }));
            Row("mapchunk", unchecked((long)ChunkPos.ToChunkIndex(4, 0, 0)), Serialize(new MapPayload { TerrainHeight = Enumerable.Repeat((ushort)35, 1024).ToArray() }));
            for (int cy = 0; cy < 3; cy++) foreach (int cx in new[] { 0, 4 }) Row("chunk", unchecked((long)ChunkPos.ToChunkIndex(cx, cy, 0)), bytes);
        }
        byte[] before = SHA256.HashData(File.ReadAllBytes(database));
        int worldLoads = 0, chunkDisposals = 0;
        var log = new List<string>();
        object logger = Proxy(typeof(ILogger), (m, a) => { if (m.Name is "Warning" or "Error" or "Notification" or "Debug") log.Add(a?[0]?.ToString() ?? ""); return m.ReturnType == typeof(bool) ? false : null; });
        object manager = Proxy(typeof(IWorldManagerAPI), (m, a) => m.Name switch {
            "get_MapSizeY" => 96,
            "get_CurrentWorldFilepath" or "get_CurrentWorldName" => database,
            "BlockingTestMapChunkExists" => true,
            "BlockingLoadChunkColumn" or "LoadChunkColumnPriority" => throw new Exception("Exporter loaded the live world: " + ++worldLoads),
            _ => throw new Exception("Unexpected world manager call: " + m.Name)
        });
        var blocks = new Block[] { new Block { BlockId = 0, Code = new AssetLocation("game:air") }, new Block { BlockId = 1, Code = new AssetLocation("game:rock-granite") }, new Block { BlockId = 2, Code = new AssetLocation("game:water-still-7") } };
        object world = Proxy(typeof(IServerWorldAccessor), (m, a) => m.Name switch {
            "get_Blocks" => blocks,
            "GetBlock" => blocks[(int)a![0]!],
            "get_SavegameIdentifier" => "memory-fixture",
            _ => throw new Exception("Terrain decoder accessed entity/world state: " + m.Name)
        });
        object api = Proxy(typeof(ICoreServerAPI), (m, a) => m.Name switch {
            "get_Server" => null,
            "get_WorldManager" => manager,
            "get_World" => world,
            "get_Logger" => logger,
            "get_DataBasePath" => temporary,
            "GetOrCreateDataPath" => temporary,
            _ => throw new Exception("Unexpected API call: " + m.Name)
        });
        ((ApiProxy)api).Server = server;
        Type readerType = suite.GetType("BlockLogger.WebMap.WebMapSaveDbTerrainReader", true)!;
        object?[] openArgs = { api, database, null };
        using var reader = (IDisposable?)readerType.GetMethod("TryOpen")!.Invoke(null, openArgs);
        Assert(reader != null, "Reader unavailable: " + openArgs[2]);
        object pool = readerType.GetField("chunkDataPool", Private)!.GetValue(reader)!;
        Assert((int)pool.GetType().GetField("CacheSize")!.GetValue(pool)! == 64, "Export pool not bounded");
        var chunks = new IServerChunk[3];
        for (int iteration = 0; iteration < 80; iteration++) {
            int cx = iteration % 2 == 0 ? 0 : 4;
            object?[] readArgs = { cx, 0, 96, chunks, null };
            Assert((bool)readerType.GetMethod("TryLoadColumn")!.Invoke(reader, readArgs)!, "Column read failed at " + cx + ",0 (iteration " + iteration + "): " + readerType.GetProperty("LastFailure")!.GetValue(reader));
            Assert(cx == 0 ? ((ushort[])readArgs[4]!).Length == 1024 && chunks[2] == null : readArgs[4] == null && chunks[2] != null, "Surface-only or missing-height fallback incorrect");
            foreach (IServerChunk? chunk in chunks) {
                if (chunk == null) continue;
                chunk.Unpack_ReadOnly();
                Assert(chunk.EntitiesCount == 0 && chunk.BlockEntities.Count == 0 && ((ServerChunk)chunk).ModData.Count == 0, "Entity/mod metadata reconstructed");
                Assert(((ServerChunk)chunk).ServerSideModdata.Count == 0, "Server mod metadata reconstructed");
                for (int i = 0; i < 32768; i++) {
                    Assert(chunk.Data.GetBlockId(i, BlockLayersAccess.Default) == expected[i], "Terrain block mismatch");
                    Assert(chunk.Data.GetBlockId(i, BlockLayersAccess.Fluid) == expectedFluid[i], "Terrain fluid mismatch");
                }
                chunk.Dispose(); chunkDisposals++;
            }
            Array.Clear(chunks);
            Assert((int)pool.GetType().GetMethod("CountFree")!.Invoke(pool, null)! <= 128, "Private chunk pool grew beyond bound");
        }
        reader!.Dispose();
        Assert((int)pool.GetType().GetMethod("CountFree")!.Invoke(pool, null)! == 0, "Disposed reader retained terrain arrays");
        Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))), "Read-only terrain reader changed save bytes");
        Console.WriteLine($"PASS: 80 detached column reads / {chunkDisposals} chunk disposals; all block/fluid IDs preserved; entity, block-entity, decor and 4 MiB of mod payload skipped; no world loads; pool bounded and freed; save unchanged.");

        // Fail a startup rebuild on a missing persisted terrain row. Existing cache and
        // dirty work must survive, and no compatibility loader may run automatically.
        using (var db = new SqliteConnection("Data Source=" + database)) {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "DELETE FROM chunk"; command.ExecuteNonQuery();
        }
        // The export reads SaveGame after opening the reader. Supply a minimal actual save object.
        object save = RuntimeHelpers.GetUninitializedObject(typeof(SaveGame));
        ((SaveGame)save).SavegameIdentifier = "memory-fixture";
        ((ApiProxy)manager).Handler = (m, a) => m.Name switch {
            "get_SaveGame" => save,
            "get_MapSizeY" => 96,
            "get_CurrentWorldFilepath" or "get_CurrentWorldName" => database,
            "BlockingTestMapChunkExists" => true,
            "BlockingLoadChunkColumn" or "LoadChunkColumnPriority" => throw new Exception("Unexpected world load: " + ++worldLoads),
            _ => throw new Exception("Unexpected manager call: " + m.Name)
        };
        string mapRoot = Path.Combine(temporary, "map"); Directory.CreateDirectory(Path.Combine(mapRoot, "terrain"));
        string sentinel = Path.Combine(mapRoot, "terrain", "existing-map.txt"); File.WriteAllText(sentinel, "preserve");
        Type configType = suite.GetType("BlockLogger.WebMap.WebMapConfig", true)!;
        object config = Activator.CreateInstance(configType)!;
        configType.GetProperty("TerrainTileSize")!.SetValue(config, 128);
        configType.GetProperty("TerrainBaseRenderWorkers")!.SetValue(config, 2);
        Type exporterType = suite.GetType("BlockLogger.WebMap.WebMapTerrainExporter", true)!;
        Type statusType = suite.GetType("BlockLogger.WebMap.WebMapTerrainExportStatus", true)!;
        var captures = new StatusCapture();
        Delegate publish = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(statusType), captures, typeof(StatusCapture).GetMethod(nameof(StatusCapture.Capture))!);
        Type journalType = suite.GetType("BlockLogger.WebMap.WebMapDirtyChunkJournal", true)!;
        using var journal = (IDisposable)Activator.CreateInstance(journalType, api)!;
        journalType.GetMethod("MarkBlockDirty")!.Invoke(journal, new object[] { 0, 0, 0 });
        object exporter = Activator.CreateInstance(exporterType, api, mapRoot, publish, journal)!;
        Assert(!(bool)exporterType.GetMethod("Export")!.Invoke(exporter, new[] { config, "startup", (object)false })!, "Corrupt terrain incorrectly published");
        Assert(File.ReadAllText(sentinel) == "preserve" && !Directory.Exists(Path.Combine(mapRoot, "terrain-next")), "Failed staging damaged current terrain");
        Assert((int)journalType.GetProperty("Count")!.GetValue(journal)! == 1, "Failed export acknowledged dirty work");
        object last = captures.Last!;
        Assert((string)statusType.GetProperty("State")!.GetValue(last)! == "failed", "Missing failure state");
        Assert((string)statusType.GetProperty("LastError")!.GetValue(last)! is string error && error.Contains("Safe terrain read failed"), "Failure was unrelated to terrain read: " + statusType.GetProperty("LastError")!.GetValue(last));
        Assert((bool)statusType.GetProperty("ExportResourcesReleased")!.GetValue(last)! && (long)statusType.GetProperty("WorldLoaderChunkColumns")!.GetValue(last)! == 0 && worldLoads == 0, "Failure retained resources or used live loader");
        Console.WriteLine("PASS: failed parallel startup rebuild keeps current map and dirty journal, removes staging, reports memory/cleanup, never calls world loaders.");
        using (var db = new SqliteConnection("Data Source=" + database)) {
            db.Open(); using var command = db.CreateCommand();
            for (int cy = 0; cy < 3; cy++) foreach (int cx in new[] { 0, 4 }) {
                command.CommandText = "INSERT INTO chunk VALUES($p,$d)";
                command.Parameters.Clear(); command.Parameters.AddWithValue("$p", unchecked((long)ChunkPos.ToChunkIndex(cx, cy, 0))); command.Parameters.AddWithValue("$d", bytes); command.ExecuteNonQuery();
            }
        }
        byte[] successBefore = SHA256.HashData(File.ReadAllBytes(database));
        bool exported = (bool)exporterType.GetMethod("Export")!.Invoke(exporter, new[] { config, "startup", (object)false })!;
        Assert(exported, "Valid startup export failed: " + statusType.GetProperty("LastError")!.GetValue(captures.Last));
        last = captures.Last!;
        Assert((string)statusType.GetProperty("TerrainReadMode")!.GetValue(last)! == "save-db-parallel-2", "Parallel terrain reader was not exercised");
        Assert((bool)statusType.GetProperty("ExportResourcesReleased")!.GetValue(last)! && (string)statusType.GetProperty("State")!.GetValue(last)! == "ready", "Successful export did not finish resource cleanup");
        Assert((int)journalType.GetProperty("Count")!.GetValue(journal)! == 0, "Successful export did not acknowledge dirty terrain");
        Assert(Directory.EnumerateFiles(Path.Combine(mapRoot, "terrain"), "*.png", SearchOption.AllDirectories).Any(), "Successful export did not write terrain tiles");
        Assert(successBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))) && worldLoads == 0, "Successful export changed save or loaded world");
        Assert((bool)exporterType.GetMethod("Export")!.Invoke(exporter, new[] { config, "startup", (object)false })!, "Incremental reuse failed");
        last = captures.Last!;
        Assert((string)statusType.GetProperty("ExportMode")!.GetValue(last)! == "incremental" && (int)statusType.GetProperty("WrittenTiles")!.GetValue(last)! == 0, "Clean startup did not reuse published tiles");
        Console.WriteLine("PASS: successful parallel startup export writes terrain/overview/pyramid, releases resources, acknowledges dirty work, leaves save/world untouched; next startup reuses unchanged tiles.");
        // Runtime automatic exploration: one saved tile per pass, no world loads, and
        // unrelated unsaved edits must remain in the journal.
        object events = Proxy(typeof(IServerEventAPI), (m, a) => {
            if (m.Name == "EnqueueMainThreadTask") { ((Action)a![0]!)(); return null; }
            throw new Exception("Unexpected runtime event " + m.Name);
        });
        var previousApiHandler = ((ApiProxy)api).Handler;
        ((ApiProxy)api).Handler = (m, a) => m.Name == "get_Event" ? events : previousApiHandler(m, a);
        configType.GetProperty("LiveTerrainMaxTilesPerPass")!.SetValue(config, 1);
        journalType.GetMethod("MarkBlockDirty")!.Invoke(journal, new object[] { 0, 1, 1 });
        void AddExploration(int cx, bool terrainRows) {
            using var db = new SqliteConnection("Data Source=" + database); db.Open(); using var command = db.CreateCommand();
            void Insert(string table, long position, byte[] data) {
                command.CommandText = "INSERT INTO " + table + " VALUES($p,$d)";command.Parameters.Clear();command.Parameters.AddWithValue("$p",position);command.Parameters.AddWithValue("$d",data);command.ExecuteNonQuery();
            }
            Insert("mapchunk", unchecked((long)ChunkPos.ToChunkIndex(cx,0,0)), Serialize(new MapPayload { Heights = Enumerable.Repeat((ushort)35,1024).ToArray() }));
            if(terrainRows)for(int cy=0;cy<3;cy++)Insert("chunk",unchecked((long)ChunkPos.ToChunkIndex(cx,cy,0)),bytes);
        }
        bool Automatic() => ((Task<bool>)exporterType.GetMethod("ExportRuntimeForcedAsync")!.Invoke(exporter,new object[]{config,CancellationToken.None,false,true})!).GetAwaiter().GetResult();
        AddExploration(8,true); AddExploration(12,true);
        byte[] runtimeBefore = SHA256.HashData(File.ReadAllBytes(database));
        string originalTile = Path.Combine(mapRoot,"terrain","0","0.png");byte[] oldTileHash=SHA256.HashData(File.ReadAllBytes(originalTile));
        for(int pass=0;pass<2;pass++){
            Assert(Automatic(),"Automatic exploration failed: "+statusType.GetProperty("LastError")!.GetValue(captures.Last));
            Assert((int)statusType.GetProperty("WrittenTiles")!.GetValue(captures.Last)! ==1,"Automatic tile bound not respected");
            using var manifest=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(mapRoot,"terrain","manifest.json")));
            Assert(manifest.RootElement.GetProperty("ChangedTiles").GetArrayLength()==1,"Changed tile revision not bounded");
        }
        Assert(Automatic()&&(int)statusType.GetProperty("WrittenTiles")!.GetValue(captures.Last)! ==0,"Already published exploration rendered again");
        Assert((int)journalType.GetProperty("Count")!.GetValue(journal)! ==1,"Automatic exploration consumed an unsaved edit");
        Assert(worldLoads==0&&runtimeBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database)))&&oldTileHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(originalTile))),"Runtime pass modified save, loaded world or rewrote unchanged base tile");
        AddExploration(1,true);
        Assert(Automatic() && (int)statusType.GetProperty("WrittenTiles")!.GetValue(captures.Last)! == 1,"New saved column inside an existing tile was missed");
        using(var manifest=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(mapRoot,"terrain","manifest.json"))))
            Assert(manifest.RootElement.GetProperty("Tiles").GetProperty("0,0").GetProperty("GeneratedChunkCount").GetInt32()==2,"Existing tile coverage not updated");
        var published=Directory.EnumerateFiles(Path.Combine(mapRoot,"terrain"),"*",SearchOption.AllDirectories).ToDictionary(f=>f,f=>SHA256.HashData(File.ReadAllBytes(f)));
        AddExploration(16,false);
        Assert(!Automatic(),"Missing saved terrain row incorrectly published");
        Assert(published.All(pair=>pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key))))&&worldLoads==0,"Failed automatic pass damaged cache or loaded live world");
        Assert((int)journalType.GetProperty("Count")!.GetValue(journal)! ==1,"Failed automatic pass consumed dirty work");
        Console.WriteLine("PASS: bounded automatic exploration publishes one new saved tile per pass, clean pass writes zero, revisions identify changed tiles, unsaved edits retained, no world loads/save writes; unreadable new terrain preserves cache.");
        // A decoder unavailable after a game update must also keep the working map.
        var mapFiles = Directory.EnumerateFiles(Path.Combine(mapRoot, "terrain"), "*", SearchOption.AllDirectories).ToDictionary(f => f, f => SHA256.HashData(File.ReadAllBytes(f)));
        journalType.GetMethod("MarkBlockDirty")!.Invoke(journal, new object[] { 0, 0, 0 });
        ((ApiProxy)api).Server = null!;
        Assert(!(bool)exporterType.GetMethod("Export")!.Invoke(exporter, new[] { config, "startup", (object)false })!, "Missing decoder silently fell back");
        Assert(mapFiles.All(pair => File.Exists(pair.Key) && pair.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(pair.Key)))) && (int)journalType.GetProperty("Count")!.GetValue(journal)! == 1, "Decoder failure changed published map or journal");
        Assert(worldLoads == 0 && (bool)statusType.GetProperty("ExportResourcesReleased")!.GetValue(captures.Last)!, "Decoder failure retained export resources or loaded world");
        Console.WriteLine("PASS: unavailable decoder preserves published map and dirty work with no automatic world-loader fallback.");
        original.Dispose(); sourcePool.FreeAll();
    }
}
public class ApiProxy : DispatchProxy {
    public ServerMain Server = null!;
    public System.Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
public class StatusCapture { public object? Last; public void Capture(object status) => Last = status; }
[ProtoContract] public class MapPayload { [ProtoMember(3)] public ushort[]? Heights { get; set; } [ProtoMember(7)] public ushort[]? TerrainHeight { get; set; } }
[ProtoContract] public class FullChunk {
    [ProtoMember(1)] public byte[]? Blocks { get; set; }
    [ProtoMember(2)] public byte[]? Light { get; set; }
    [ProtoMember(3)] public byte[]? SaturatedLight { get; set; }
    [ProtoMember(5)] public int EntitiesCount { get; set; }
    [ProtoMember(6)] public List<byte[]>? Entities { get; set; }
    [ProtoMember(7)] public int BlockEntitiesCount { get; set; }
    [ProtoMember(8)] public List<byte[]>? BlockEntities { get; set; }
    [ProtoMember(9)] public Dictionary<string, byte[]>? ModData { get; set; }
    [ProtoMember(11)] public Dictionary<string, byte[]>? ServerModData { get; set; }
    [ProtoMember(12)] public string Version { get; set; } = "";
    [ProtoMember(13)] public bool Empty { get; set; }
    [ProtoMember(14)] public byte[]? Decors { get; set; }
    [ProtoMember(15)] public int CompressionVersion { get; set; }
    [ProtoMember(16)] public byte[]? Fluids { get; set; }
}
