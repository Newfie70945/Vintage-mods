using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using BlockLogger.Admin;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;
using Vintagestory.Server;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string dll = Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY");
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { Path.GetDirectoryName(dll)!, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string path = Path.Combine(root, name.Name + ".dll"); if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Run();

[MethodImpl(MethodImplOptions.NoInlining)] static void Run()
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    void Set(object target, string field, object value) => target.GetType().GetField(field, flags)!.SetValue(target, value);
    object? Call(object target, string method, params object?[] args) => target.GetType().GetMethods(flags).Single(x => x.Name == method && x.GetParameters().Length == args.Length).Invoke(target, args);
    RuntimeHelpers.RunModuleConstructor(typeof(AdminStorageService).Module.ModuleHandle);
    string root = Path.Combine(Path.GetTempPath(), "nss-admin-memory-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var fixture = new Fixture(root);
        var api = (ICoreServerAPI)fixture.Make(typeof(ICoreServerAPI))!;
        var storage = (AdminStorageService)RuntimeHelpers.GetUninitializedObject(typeof(AdminStorageService));
        string database = Path.Combine(root, "essentials.db");
        Set(storage, "api", api); Set(storage, "dbPath", database); Set(storage, "backupsPath", root);
        Set(storage, "flushGate", new object()); Set(storage, "saveSignal", new AutoResetEvent(false));
        Set(storage, "persistedOfflineInventoryTimes", new Dictionary<string, long>());
        Call(storage, "InitializeAndMigrate");

        var state = new AdminState();
        byte[] attributes = Enumerable.Range(0, 128 * 1024).Select(x => (byte)(x % 251)).ToArray();
        for (int player = 0; player < 8; player++)
        {
            string uid = "player-" + player;
            state.Players[uid] = new PlayerAdminRecord { PlayerUid=uid, LastKnownName=uid };
            var snapshots = new List<AdminInventorySnapshotRecord>();
            for (int index = 0; index < 12; index++)
            {
                long id = player * 12 + index + 1;
                snapshots.Add(new AdminInventorySnapshotRecord { Id=id, UnixMilliseconds=id, PlayerUid=uid, OccupiedSlots=2,
                    Slots=new() {
                        new() { Inventory="hotbar-" + uid, Slot=0, ItemCode="game:stone", StackSize=1, StackBytes=attributes },
                        new() { Inventory="hotbar-" + uid, Slot=1, ItemCode="game:stone", StackSize=2, StackBytes=attributes }
                    } });
            }
            state.InventorySnapshots[uid] = snapshots;
        }
        Call(storage, "PersistState", state);
        AdminState loaded = storage.LoadState();
        Check(loaded.InventorySnapshots.Values.Sum(x => x.Count) == 96, "Snapshot headers were lost");
        Check(loaded.InventorySnapshots.Values.SelectMany(x => x).All(x => !x.SlotsPayloadLoaded && x.Slots.Count == 0), "Startup retained historical payloads");
        var header = loaded.InventorySnapshots["player-0"][0];
        byte[] beforeRead = SHA256.HashData(File.ReadAllBytes(database));
        Check(storage.TryReadInventorySnapshot(header, out var read, out _) && read!.Slots.Count == 2 && read.Slots.All(x => x.StackBytes.SequenceEqual(attributes)), "On-demand snapshot lost attributes");
        Check(beforeRead.SequenceEqual(SHA256.HashData(File.ReadAllBytes(database))), "Read-only snapshot lookup changed storage");
        Check(header.Slots.Count == 0 && !header.SlotsPayloadLoaded, "Read request republished payloads into resident state");
        Call(storage, "PersistState", loaded);
        Check(storage.TryReadInventorySnapshot(header, out read, out _) && read!.Slots.All(x => x.StackBytes.SequenceEqual(attributes)), "Saving unloaded headers replaced inventory blobs");
        Console.WriteLine("PASS: 96 saved snapshots (24 MiB attributes) load as headers; reads preserve exact attributes and leave storage unchanged; saving unloaded headers preserves blobs.");

        var admin = (AdminService)RuntimeHelpers.GetUninitializedObject(typeof(AdminService));
        Set(admin, "gate", new object()); Set(admin, "state", loaded); Set(admin, "api", api);
        storage.InventorySnapshotsCommitted += high => Call(admin, "ReleasePersistedSnapshotPayloads", high);
        var dirty = new AdminInventorySnapshotRecord { Id=97, UnixMilliseconds=97, PlayerUid="player-0", OccupiedSlots=1,
            Slots=new() { new() { Inventory="hotbar-player-0", Slot=0, ItemCode="game:stone", StackSize=1, StackBytes=attributes } } };
        loaded.InventorySnapshots["player-0"].Add(dirty);
        Set(storage, "dbPath", root);
        try { Call(storage, "PersistState", loaded); throw new Exception("Expected injected save failure"); }
        catch (TargetInvocationException ex) when (ex.InnerException is SqliteException) { }
        Check(dirty.SlotsPayloadLoaded && dirty.Slots.Count == 1, "Failed save discarded the new rollback payload");
        Set(storage, "dbPath", database); Call(storage, "PersistState", loaded);
        Check(!dirty.SlotsPayloadLoaded && dirty.Slots.Count == 0, "Committed payload was retained");
        Check(storage.TryReadInventorySnapshot(dirty, out read, out _) && read!.Slots[0].StackBytes.SequenceEqual(attributes), "Released payload cannot be restored from storage");
        using (var conn = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            conn.Open(); using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM inventory_snapshot_slots WHERE snapshot_id=97;"; cmd.ExecuteNonQuery();
        }
        Check(!storage.TryReadInventorySnapshot(dirty, out read, out _) && read == null, "Incomplete storage published a partial snapshot");
        Console.WriteLine("PASS: failed persistence retains dirty stacks; successful commit releases them; readback remains exact and missing slots fail closed.");

        Item stone = new() { ItemId=1, Code=new AssetLocation("game:stone"), MaxStackSize=64 };
        fixture.Stone = stone;
        var inventory = new InventoryGeneric(3, "hotbar-player-0", api);
        inventory[0].Itemstack = new ItemStack(stone, 7);
        var second = new InventoryGeneric(2, "offhand-player-0", api);
        second[0].Itemstack = new ItemStack(stone, 3);
        var bags = new InventoryPlayerBackpacks("backpack-player-0", api);
        IServerPlayer playerProxy = TestPlayer.Create(inventory, second, bags);
        Set(admin, "onlineGate", new object());
        Set(admin, "onlinePlayersByUid", new Dictionary<string, IServerPlayer>(StringComparer.OrdinalIgnoreCase) { ["player-0"]=playerProxy });
        Set(admin, "onlinePlayersByName", new Dictionary<string, IServerPlayer>(StringComparer.OrdinalIgnoreCase) { ["player-0"]=playerProxy });
        var bad = new AdminInventorySnapshotRecord { Id=98, PlayerUid="player-0", OccupiedSlots=1,
            Slots=new() { new() { Inventory=inventory.InventoryID, Slot=0, StackBytes=new byte[] { 1, 2, 3 } } } };
        loaded.InventorySnapshots["player-0"].Add(bad);
        string result = admin.RestoreInventorySnapshot("owner", "player-0", 98);
        Check(result.Contains("No inventory was changed") && inventory[0].StackSize == 7 && second[0].StackSize == 3, "Malformed snapshot changed a live inventory");
        bad.Slots[0].StackBytes = new ItemStack(stone, 2).ToBytes(); bad.Slots[0].Slot = 100;
        result = admin.RestoreInventorySnapshot("owner", "player-0", 98);
        Check(result.Contains("No inventory was changed") && inventory[0].StackSize == 7 && second[0].StackSize == 3, "Incompatible capacity changed a live inventory");
        bad.Slots[0].Inventory = bags.InventoryID; bad.Slots[0].Slot = 8;
        result = admin.RestoreInventorySnapshot("owner", "player-0", 98);
        Check(result.Contains("No inventory was changed") && inventory[0].StackSize == 7 && second[0].StackSize == 3 && bags.Count == 4, "Incompatible saved bags changed a live inventory");
        bad.Slots[0].Inventory = inventory.InventoryID; bad.Slots[0].Slot = 0;
        object?[] prepareArgs = { playerProxy, bad, null, null };
        Check((bool)Call(admin, "TryPrepareSnapshotRestore", prepareArgs)!, "Valid snapshot failed preflight");
        Call(admin, "ApplyPreparedSnapshot", playerProxy, prepareArgs[2]);
        Check(inventory[0].StackSize == 2 && second[0].Empty, "Validated snapshot did not restore exact inventory");
        Console.WriteLine("PASS: malformed stacks and incompatible capacity leave both live inventories unchanged; valid preflight and apply restore exact stack counts.");

        foreach (string invalid in new[] { "NaNh", "Infinityd", "-Infinitym", "1e309w", "1e20s", "-2h" })
            Check(!AdminService.TryParseDuration(invalid, out _, out _), "Unsafe duration accepted: " + invalid);
        Check(AdminService.TryParseDuration("1.5h", out long ms, out bool permanent) && ms == 5_400_000 && !permanent, "Fractional durations regressed");
        Check(AdminService.TryParseDuration("permanent", out _, out permanent) && permanent, "Permanent moderation regressed");
        Check(!string.IsNullOrWhiteSpace(AdminService.FormatDuration(long.MaxValue)), "Largest direct delay crashes formatting");
        Check((long)Call(admin, "DeadlineAfterMilliseconds", long.MaxValue)! == long.MaxValue, "Largest direct delay overflowed deadline");
        Console.WriteLine("PASS: finite fractional/permanent durations work; NaN, infinity and excessive values reject; direct long.MaxValue delays saturate safely.");
        ((AutoResetEvent)typeof(AdminStorageService).GetField("saveSignal", flags)!.GetValue(storage)!).Dispose();
    }
    finally { Directory.Delete(root, true); }
}

public class ApiProxy : DispatchProxy
{
    public Fixture Fixture = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
        "GetOrCreateDataPath" => Fixture.Root,
        "get_SavegameIdentifier" => "admin-memory-check",
        "get_PlayerUID" or "get_PlayerName" => "player-0",
        "GetItem" when args?.FirstOrDefault() is int id => id == 1 ? Fixture.Stone : null,
        "GetItem" when args?.FirstOrDefault() is AssetLocation location => location.ToString() == "game:stone" ? Fixture.Stone : null,
        _ => Fixture.Make(method.ReturnType)
    };
}
public class Fixture(string root)
{
    public string Root = root;
    public Item? Stone;
    private readonly Dictionary<Type, object> proxies = new();
    public object? Make(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return "";
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (!type.IsInterface) return type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : null;
        if (proxies.TryGetValue(type, out var cached)) return cached;
        object proxy = DispatchProxy.Create(type, typeof(ApiProxy)); ((ApiProxy)proxy).Fixture = this; proxies[type] = proxy; return proxy;
    }
}

public sealed class TestPlayer : ServerPlayer
{
    private TestPlayer() : base(null!, null!) { }
    public override string PlayerUID => "player-0";
    public static TestPlayer Create(params InventoryBase[] inventories)
    {
        var player = (TestPlayer)RuntimeHelpers.GetUninitializedObject(typeof(TestPlayer));
        var manager = (ServerPlayerInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayerInventoryManager));
        manager.Inventories = new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>();
        foreach (var inventory in inventories) manager.Inventories.Add(inventory.InventoryID, inventory);
        typeof(ServerPlayer).GetField("inventoryMgr", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(player, manager);
        return player;
    }
}
