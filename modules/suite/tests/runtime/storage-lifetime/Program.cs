using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string dll = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { Path.GetDirectoryName(dll)!, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") }) {
        string path = Path.Combine(root, name.Name + ".dll"); if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
Assembly suite = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
RuntimeHelpers.RunModuleConstructor(suite.ManifestModule.ModuleHandle);
Type T(string name) => suite.GetType("BlockLogger." + name, true)!;
object Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value)!;
void Set(object value, string name, object? data) {
    var property = value.GetType().GetProperty(name, Flags);
    if (property != null) property.SetValue(value, data); else value.GetType().GetField(name, Flags)!.SetValue(value, data);
}
object? Call(object value, string name, params object?[] args) => value.GetType().GetMethods(Flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length).OrderBy(m => m.DeclaringType == value.GetType() ? 0 : 1).First().Invoke(value, args);
void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
string temporary = Path.Combine(Path.GetTempPath(), "nss-storage-lifetime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var fixture = new Fixture(temporary);
string mode = args.FirstOrDefault() ?? "all";
try {
    if (mode is "all" or "query") {
        Type storageType = T("Services.LogStorageService");
        object exactStorage = RuntimeHelpers.GetUninitializedObject(storageType);
        Set(exactStorage, "currentWorldId", "fixture-world");
        var sqlAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(dll)!, "Microsoft.Data.Sqlite.dll"));
        Type connType = sqlAssembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
        using var sqlConnection = (IDisposable)Activator.CreateInstance(connType, "Data Source=:memory:")!;
        Call(sqlConnection, "Open");
        using var sqlCommand = (IDisposable)Call(sqlConnection, "CreateCommand")!;
        Set(sqlCommand, "CommandText", "CREATE TABLE events(timestamp_ticks INTEGER,ingame_date TEXT,action INTEGER,player_uid TEXT,player_name TEXT,world_id TEXT,dimension TEXT,x INTEGER,y INTEGER,z INTEGER,block_code TEXT,meta_json TEXT);" +
            "INSERT INTO events VALUES(638000000000000000,'',1,'UID-EXACT','RenamedPlayer','fixture-world','0',1,2,3,'game:stone','{}')," +
            "(638000000000000001,'',1,'other','UID-EXACT','fixture-world','0',1,2,3,'game:stone','{}')," +
            "(638000000000000002,'',1,'uid-exact','OtherAccount','fixture-world','0',1,2,3,'game:stone','{}');");
        Call(sqlCommand, "ExecuteNonQuery");
        Type recordType = T("Models.LogEventRecord"), requestType = storageType.GetNestedType("EventRequest", BindingFlags.NonPublic)!;
        object completion = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(typeof(List<>).MakeGenericType(recordType)))!;
        object request = requestType.GetConstructors(Flags).Single(c => c.GetParameters().Length == 14).Invoke(new object?[]{0,0,0,0,0,100,0,null,null,false,true,"UID-EXACT",0L,completion});
        Set(request, "ExactUid", true);
        var matching = (IList)Call(exactStorage, "QueryPlayerEvents", sqlConnection, request)!;
        Check(matching.Count == 1 && (string)matching[0]!.GetType().GetProperty("PlayerName")!.GetValue(matching[0])! == "RenamedPlayer", "Exact investigation history included a UID-like name/case-distinct account or lost renamed history");
        Console.WriteLine("PASS: exact account history preserves renamed-player rows and excludes UID-like names and case-distinct account IDs.");
    }
    if (mode is "all" or "query") {
        Type type = T("Services.LogStorageService");
        foreach (bool failedMigration in new[] { false, true }) {
            object storage = RuntimeHelpers.GetUninitializedObject(type);
            Set(storage, "api", fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType));
            Set(storage, "dbPath", Path.Combine(temporary, "queued-" + failedMigration + ".db"));
            // A real, readable database ensures the cancellation case reaches the
            // consuming enumerable, rather than failing earlier while opening storage.
            using var initialConnection = (IDisposable)Call(storage, "OpenConnection")!;
            var cts = new CancellationTokenSource(); Set(storage, "cts", cts);
            foreach (string field in new[] { "queryQueue", "snapshotQueryQueue" }) {
                Type queueType = type.GetField(field, Flags)!.FieldType;
                Set(storage, field, Activator.CreateInstance(queueType, 64)!);
            }
            Set(storage, "migrationTask", failedMigration ? Task.FromException(new IOException("Injected migration failure")) : Task.CompletedTask);
            var tasks = new List<Task>();
            for (int i = 0; i < 32; i++) {
                tasks.Add((Task)Call(storage, "QueryEventsAsync", 0, 0, 0, 0, 0, 10, null, null, false, 0)!);
                tasks.Add((Task)Call(storage, "QueryPlayerEventsAsync", "player", 10, null, 0)!);
                tasks.Add((Task)Call(storage, "QuerySnapshotsAsync", 0, 0, 0, 0, 0, 10, null)!);
            }
            if (!failedMigration) cts.Cancel();
            Call(storage, "QueryLoop", cts.Token); Call(storage, "SnapshotQueryLoop", cts.Token);
            Check(tasks.All(x => x.IsCompleted), "Stopped query workers retained unresolved requests");
            foreach (Task task in tasks) _ = task.Exception;
            var late = (Task)Call(storage, "QueryEventsAsync", 0, 0, 0, 0, 0, 10, null, null, false, 0)!;
            Check(late.IsFaulted, "A stopped query worker accepted a request that cannot finish"); _ = late.Exception;
            Check((int)Field(storage, "queryQueue").GetType().GetProperty("Count")!.GetValue(Field(storage, "queryQueue"))! == 0, "Stopped event query queue retained requests");
            foreach (string field in new[] { "queryQueue", "snapshotQueryQueue" }) ((IDisposable)Field(storage, field)).Dispose();
            var afterDispose = (Task)Call(storage, "QuerySnapshotsAsync", 0, 0, 0, 0, 0, 10, null)!;
            Check(afterDispose.IsFaulted, "Disposed queue did not return a completed failure"); _ = afterDispose.Exception;
            cts.Dispose();
        }
        Console.WriteLine("PASS: cancellation/migration failure completes all 96 waiting queries, empties both queues and rejects requests after worker stop or queue disposal.");
        object live = Activator.CreateInstance(type, fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType), 100, 1000, 100, false, 0, 2)!;
        try {
            using var connection = (IDisposable)Call(live, "OpenConnection")!;
            using var command = (IDisposable)Call(connection, "CreateCommand")!;
            Set(command, "CommandText", "DROP TABLE events;"); Call(command, "ExecuteNonQuery");
            Task broken = (Task)Call(live, "QueryEventsAsync", 0, 0, 0, 0, 0, 10, null, null, false, 0)!;
            Check(SpinWait.SpinUntil(() => broken.IsCompleted, 5000) && broken.IsFaulted, "A failed SQLite query was reported as a successful empty search"); _ = broken.Exception;
        } finally { ((IDisposable)live).Dispose(); ((IDisposable)live).Dispose(); }
        Task stopped = (Task)Call(live, "QueryPlayerEventsAsync", "player", 10, null, 0)!;
        Check(stopped.IsFaulted, "Disposed logger accepted a new query"); _ = stopped.Exception;
        Console.WriteLine("PASS: an injected SQLite failure reports an error instead of an empty search; logger shutdown is idempotent and rejects subsequent queries.");
    }
    if (mode is "all" or "journal") {
        Type type = T("WebMap.WebMapDirtyChunkJournal");
        object journal = Activator.CreateInstance(type, fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType))!;
        // Hold the background writer so injected disk failure and queue retention are deterministic.
        Set(journal, "worker", Task.CompletedTask);
        string path = (string)Field(journal, "path");
        try {
            for (int i = 0; i < 10_000; i++) Call(journal, "MarkBlockDirty", 0, i * 32, 0);
            Directory.CreateDirectory(path);
            try { Call(journal, "FlushPendingWrites"); throw new Exception("Expected append failure"); }
            catch (TargetInvocationException e) when (e.InnerException is IOException or UnauthorizedAccessException) { }
            Check((int)Field(journal, "pendingPackedWrites").GetType().GetProperty("Count")!.GetValue(Field(journal, "pendingPackedWrites"))! == 10_000, "Failed dirty-journal append discarded accepted changes");
            Check(((SemaphoreSlim)Field(journal, "wake")).CurrentCount <= 1, "Dirty burst queued redundant writer wakeups");
            Directory.Delete(path); File.WriteAllText(path, "v1|storage-audit|0|unfinished"); Call(journal, "FlushPendingWrites");
            using (var recovered = (IDisposable)Activator.CreateInstance(type, fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType))!)
                Check((int)type.GetProperty("Count")!.GetValue(recovered)! == 10_000, "Retry after an interrupted row did not persist every coordinate");
            object snapshot = Call(journal, "Snapshot")!;
            long generation = (long)type.GetProperty("MutationVersion")!.GetValue(journal)!;
            Call(journal, "MarkBlockDirty", 0, 0, 0); // Same coordinate changed again after export.
            Check(!(bool)Call(journal, "TryAcknowledgeSnapshot", snapshot, generation)!, "Export acknowledgement removed a newer edit at the same coordinate");
            generation = (long)type.GetProperty("MutationVersion")!.GetValue(journal)!;
            Directory.CreateDirectory(path + ".tmp");
            try { Call(journal, "TryAcknowledgeSnapshot", snapshot, generation); throw new Exception("Expected rewrite failure"); }
            catch (TargetInvocationException e) when (e.InnerException is IOException or UnauthorizedAccessException) { }
            Check((int)type.GetProperty("Count")!.GetValue(journal)! == 10_000, "Failed compaction discarded in-memory dirty changes");
            Directory.Delete(path + ".tmp");
            Check((bool)Call(journal, "TryAcknowledgeSnapshot", snapshot, generation)! && (int)type.GetProperty("Count")!.GetValue(journal)! == 0, "Successful acknowledgement did not compact the journal");
            Check(File.ReadAllText(path).Length == 0, "Acknowledged coordinates persisted in the rewritten journal");
            long baseline = (long)type.GetProperty("MutationVersion")!.GetValue(journal)!;
            Task edits = Task.Run(() => { for (int i = 0; i < 2000; i++) { Call(journal, "MarkBlockDirty", 0, i * 32, 0); if (i % 10 == 0) Thread.Yield(); } });
            for (int i = 0; i < 100; i++) {
                object?[] snapshotArgs = { 0L }; object atomic = Call(journal, "Snapshot", snapshotArgs)!;
                Check((int)atomic.GetType().GetProperty("Count")!.GetValue(atomic)! == (long)snapshotArgs[0]! - baseline, "Concurrent snapshot and generation disagree");
                await Task.Yield();
            }
            await edits; Call(journal, "FlushPendingWrites");
            snapshot = Call(journal, "Snapshot")!; generation = (long)type.GetProperty("MutationVersion")!.GetValue(journal)!;
            object fileGate = Field(journal, "fileGate"); using var ackStarted = new ManualResetEventSlim(); Task<bool> ack;
            Monitor.Enter(fileGate);
            try {
                ack = Task.Run(() => { ackStarted.Set(); return (bool)Call(journal, "TryAcknowledgeSnapshot", snapshot, generation)!; });
                Check(ackStarted.Wait(5000), "Acknowledgement did not start");
                Call(journal, "MarkBlockDirty", 0, 0, 0);
            } finally { Monitor.Exit(fileGate); }
            Check(!await ack && (int)type.GetProperty("Count")!.GetValue(journal)! == 2000, "Acknowledgement waiting on disk erased a newer edit");
            using var reload = (IDisposable)Activator.CreateInstance(type, fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType))!;
            Check((int)type.GetProperty("Count")!.GetValue(reload)! == 2000, "Concurrent compaction changed persisted dirty work");
        } finally { ((IDisposable)journal).Dispose(); }
        Console.WriteLine("PASS: 10,000 dirty coordinates survive append failure and retry; wakeups coalesce; later edits and failed compaction retain dirty work; successful compaction removes only acknowledged work.");
        Console.WriteLine("PASS: concurrent snapshots carry matching generations; acknowledgement blocked on disk cannot erase a newer same-coordinate edit; journal reload preserves those coordinates.");
        var retryFixture = new Fixture(Path.Combine(temporary, "retry")); Directory.CreateDirectory(retryFixture.Root);
        using var retryJournal = (IDisposable)Activator.CreateInstance(type, retryFixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType))!;
        string retryPath = (string)Field(retryJournal, "path"); Directory.CreateDirectory(retryPath);
        Call(retryJournal, "MarkBlockDirty", 65536, int.MaxValue, int.MinValue);
        Check(SpinWait.SpinUntil(() => Volatile.Read(ref retryFixture.Warnings) > 0, 3000), "Background append failure was not observed");
        Directory.Delete(retryPath);
        Check(SpinWait.SpinUntil(() => File.Exists(retryPath) && File.ReadAllText(retryPath).Contains("v1|storage-audit|65536|"), 8000), "Background journal did not retry without another edit");
        Console.WriteLine("PASS: the real journal worker automatically retries an overflow-coordinate append after disk recovery, without another edit.");
    }
    if (mode is "all" or "admin") {
        Type type = T("Admin.AdminStorageService"), stateType = T("Admin.AdminState");
        object storage = RuntimeHelpers.GetUninitializedObject(type);
        Set(storage, "api", fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType));
        Set(storage, "dbPath", Path.Combine(temporary, "admin.db")); Set(storage, "backupsPath", temporary);
        Set(storage, "flushGate", new object()); Set(storage, "saveSignal", new AutoResetEvent(false));
        Set(storage, "persistedOfflineInventoryTimes", new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));
        Call(storage, "InitializeAndMigrate");
        string version = "old"; bool pause = true;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        object Snapshot() {
            object row = Activator.CreateInstance(stateType)!; Set(row, "MaintenanceReason", version);
            if (pause) { entered.Set(); Check(release.Wait(5000), "Snapshot fixture timed out"); }
            return row;
        }
        Type providerType = type.GetField("snapshotProvider", Flags)!.FieldType;
        var provider = Expression.Lambda(providerType, Expression.Convert(Expression.Invoke(Expression.Constant((Func<object>)Snapshot)), stateType)).Compile();
        Set(storage, "snapshotProvider", provider); Set(storage, "dirty", true);
        Task flush = Task.Run(() => Call(storage, "Flush"));
        try {
            Check(entered.Wait(5000), "Flush did not capture snapshot"); version = "new"; Call(storage, "RequestSave"); release.Set();
            Check(flush.Wait(5000), "Flush did not finish");
            Check((bool)Field(storage, "dirty"), "Flush erased a save request made during its older snapshot");
            pause = false; Call(storage, "Flush");
            Check(!(bool)Field(storage, "dirty"), "Successful latest snapshot remained dirty");
            object loaded = Call(storage, "LoadState")!;
            Check((string)stateType.GetProperty("MaintenanceReason")!.GetValue(loaded)! == "new", "Latest admin state was not persisted");
            // Failed saves must remain dirty for the next successful flush.
            Set(storage, "dbPath", temporary); Call(storage, "RequestSave"); Call(storage, "Flush");
            Check((bool)Field(storage, "dirty"), "Failed admin persistence cleared the retry flag");
        } finally { release.Set(); await flush; ((AutoResetEvent)Field(storage, "saveSignal")).Dispose(); }
        Console.WriteLine("PASS: a save request arriving during an older admin flush survives and persists the latest state; failed persistence retains retry state.");
    }
} finally { Directory.Delete(temporary, true); }

public class ApiProxy : DispatchProxy {
    public Fixture Fixture = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch {
        "GetOrCreateDataPath" => Fixture.Root,
        "get_SavegameIdentifier" => "storage-audit",
        "Warning" => Fixture.Warning(),
        _ => Fixture.Make(method.ReturnType)
    };
}
public class Fixture(string root) {
    public string Root = root;
    public int Warnings;
    public object? Warning() { Interlocked.Increment(ref Warnings); return null; }
    private readonly Dictionary<Type, object> proxies = new();
    public object? Make(Type type) {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return "";
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (!type.IsInterface) return type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : null;
        if (proxies.TryGetValue(type, out var cached)) return cached;
        object proxy = DispatchProxy.Create(type, typeof(ApiProxy)); ((ApiProxy)proxy).Fixture = this; proxies[type] = proxy; return proxy;
    }
}
