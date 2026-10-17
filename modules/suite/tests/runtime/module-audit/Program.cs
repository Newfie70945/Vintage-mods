// Production-assembly safety regressions for the repaired audit findings.
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

string game = Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY"));
string dll = Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY"));
AssemblyLoadContext.Default.Resolving += (context, name) => {
    foreach (string root in new[] { game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods"), Path.GetDirectoryName(dll)! }) {
        string file = Path.Combine(root, name.Name + ".dll");
        if (File.Exists(file)) return context.LoadFromAssemblyPath(file);
    }
    return null;
};
var suite = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var engine = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game, "VintagestoryLib.dll"));
const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
Type M(string name) => suite.GetType("BlockLogger." + name, true)!;
object? Call(object service, string name, params object?[] args) => service.GetType().GetMethod(name, members)!.Invoke(service, args);
object Field(object service, string name) => service.GetType().GetField(name, members)!.GetValue(service)!;
object NewService(Type type, AuditFixture fixture, params object?[] trailing) => type.GetConstructors().Single().Invoke(new[] {fixture.Make(type.GetConstructors().Single().GetParameters()[0].ParameterType)}.Concat(trailing).ToArray());
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string temporary = Path.Combine(Path.GetTempPath(), "nss-module-audit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var disposables = new List<IDisposable>();
try {
    object player = RuntimeHelpers.GetUninitializedObject(engine.GetType("Vintagestory.Server.ServerPlayer", true)!);
    var worldField = player.GetType().GetField("worlddata", members)!;
    object worldData = RuntimeHelpers.GetUninitializedObject(worldField.FieldType);
    worldField.FieldType.GetField("PlayerUID", members)!.SetValue(worldData, "audit-new-player");
    worldField.SetValue(player, worldData);

    var fixture = new AuditFixture(temporary);
    var teleportType = M("PlayerCommands.PlayerTeleportService");
    var config = Activator.CreateInstance(M("PlayerCommands.PlayerCommandConfig"))!;
    config.GetType().GetProperty("BackEnabled")!.SetValue(config, true);
    config.GetType().GetProperty("BackTrackTeleports")!.SetValue(config, true);
    Type providerType = teleportType.GetConstructors().Single().GetParameters()[1].ParameterType;
    var provider = Expression.Lambda(providerType, Expression.Constant(config)).Compile();
    var teleports = NewService(teleportType, fixture, provider, null); disposables.Add((IDisposable)teleports);
    fixture.SaveBytes = Encoding.UTF8.GetBytes("{\"Players\":[{\"PlayerUid\":\"audit-old-player\",\"Homes\":[{\"Name\":\"home\",\"Position\":{\"X\":100}}]}]}");
    Call(teleports, "Load");
    Check(((IDictionary)Field(teleports, "profiles")).Count == 1, "Valid home fixture did not load");
    byte[] originalHomes = fixture.SaveBytes!.ToArray();
    fixture.ThrowReads = true;
    Call(teleports, "Load");
    Check(((IDictionary)Field(teleports, "profiles")).Count == 1 && fixture.Errors > 0, "Failed home load discarded cached profiles");
    var originMethod = teleportType.GetMethods().Single(m => m.Name == "RecordTeleportOrigin" && m.GetParameters().Length == 3);
    object origin = Activator.CreateInstance(originMethod.GetParameters()[1].ParameterType)!;
    originMethod.Invoke(teleports, new[] {player, origin, "audit fixture"});
    Check(!(bool)Call(teleports, "Save")! && fixture.SaveBytes!.SequenceEqual(originalHomes), "Unreadable home store was overwritten");
    fixture.ThrowReads = false;
    Call(teleports, "Load");
    originMethod.Invoke(teleports, new[] {player, origin, "audit fixture"});
    Check(fixture.SaveBytes!.SequenceEqual(originalHomes), "Back tracking synchronously wrote the whole database");
    fixture.ThrowWrites = true;
    Check(!(bool)Call(teleports, "Save")! && fixture.SaveBytes!.SequenceEqual(originalHomes), "Failed save reported success or changed saved homes");
    fixture.ThrowWrites = false;
    Check((bool)Call(teleports, "Save")!, "Retry did not save homes");
    Check(Encoding.UTF8.GetString(fixture.SaveBytes!).Contains("audit-old-player") && Encoding.UTF8.GetString(fixture.SaveBytes!).Contains("audit-new-player"), "Retry lost existing homes");
    Console.WriteLine("PASS H1/A4/A5: failed home reads retain cache and original bytes, block writes; failed saves return false; back tracking batches writes and a retry preserves existing profiles.");

    var reports = NewService(M("Reports.ReportsService"), fixture); disposables.Add((IDisposable)reports);
    string reportPath = (string)Field(reports, "statePath");
    File.WriteAllText(reportPath, "{broken report state");
    Call(reports, "Load");
    var createResult = Call(reports, "CreateReport", player, "Temporary audit issue")!;
    Check((bool)createResult.GetType().GetField("Item1")!.GetValue(createResult)!, "Fixture report was not created");
    Call(reports, "OnWorldSave");
    string recovered = (string)reports.GetType().GetProperty("RecoveryFile")!.GetValue(reports)!;
    Check(File.ReadAllText(recovered) == "{broken report state", "Damaged report original was not preserved exactly");
    using (var saved = JsonDocument.Parse(File.ReadAllText(reportPath)))
        Check(saved.RootElement.GetProperty("reports").GetArrayLength() == 1, "Recovered report queue did not persist");
    for (int i = 0; i < 1000; i++) Call(reports, "AddNote", 1L, "audit-staff", new string('x', 1200));
    Check(((string)Call(reports, "AddNote", 1L, "audit-staff", "one too many")!).StartsWith("Cannot"), "Report note cap not enforced");
    var snapshot = Call(reports, "GetSnapshot", "all", 1)!;
    object row = ((IList)snapshot.GetType().GetProperty("Reports")!.GetValue(snapshot)!)[0]!;
    Check(((IList)row.GetType().GetProperty("Notes")!.GetValue(row)!).Count == 2 && ((IList)row.GetType().GetProperty("Events")!.GetValue(row)!).Count == 5, "Summary included unbounded histories");
    Check((int)row.GetType().GetProperty("NoteCount")!.GetValue(row)! == 1000, "Summary counts lost full history");
    object page = Call(reports, "GetReport", 1L, 900, 100)!;
    Check(((IList)page.GetType().GetProperty("Notes")!.GetValue(page)!).Count == 100, "History paging lost older notes");
    Call(reports, "OnWorldSave");
    using (var saved = JsonDocument.Parse(File.ReadAllText(reportPath))) {
        var report = saved.RootElement.GetProperty("reports")[0];
        Check(report.GetProperty("notes").GetArrayLength() == 1000, "Persistence truncated investigation notes");
        Check(report.GetProperty("events")[1].GetProperty("detail").GetString() == "Private staff note added.", "Note text was duplicated in events");
    }
    File.WriteAllText(reportPath, "{\"schemaVersion\":99,\"reports\":[]}");
    Call(reports, "Load");
    Check(!(bool)Field(reports,"storageReady") && !(bool)Call(reports, "CreateReport", player, "blocked")!.GetType().GetField("Item1")!.GetValue(Call(reports, "CreateReport", player, "blocked"))!, "Future schema did not block writes");
    Call(reports, "Save");
    Check(File.ReadAllText(reportPath).Contains("99"), "Future schema was overwritten");
    Console.WriteLine("PASS H2/H3/A7: verified recovery originals, future-schema write inhibition, capped new notes, bounded summaries, paged full history and durable background snapshots.");
    string recoveryDirectory = Path.Combine(Path.GetDirectoryName(reportPath)!, "recovery");
    Directory.Delete(recoveryDirectory, true); File.WriteAllText(recoveryDirectory, "Injected recovery-path barrier");
    File.WriteAllText(reportPath, "{damaged but cannot preserve"); Call(reports,"Load");
    Check(!(bool)Field(reports,"storageReady"),"Failed recovery copy did not block writes");
    Call(reports,"Save");Check(File.ReadAllText(reportPath)=="{damaged but cannot preserve","Failed recovery overwrote original");
    File.Delete(recoveryDirectory);
    using(var oversized=new FileStream(reportPath,FileMode.Create,FileAccess.Write))oversized.SetLength(64L*1024*1024+1);
    Call(reports,"Load");Call(reports,"Save");
    Check(!(bool)Field(reports,"storageReady")&&new FileInfo(reportPath).Length==64L*1024*1024+1,"Oversized original was overwritten");
    Console.WriteLine("PASS H2: recovery-copy failure and oversized state keep original files untouched and block writes.");

    var analytics = NewService(M("Analytics.PlayerCountAnalyticsService"), fixture); disposables.Add((IDisposable)analytics);
    string analyticsPath=(string)Field(analytics,"statePath");
    string today=DateTime.Now.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
    string analyticsOriginal="{\"schemaVersion\":1,\"daily\":[{\"date\":\""+today+"\",\"observedMilliseconds\":1000},{\"date\":\""+today+"\",\"observedMilliseconds\":2000},null],\"hourly\":[{\"date\":\""+today+"\",\"hour\":30},{\"date\":\""+today+"\",\"hour\":4}]}";
    File.WriteAllText(analyticsPath,analyticsOriginal);Call(analytics,"Load");
    object analyticsState=Field(analytics,"state");
    Check(((IList)analyticsState.GetType().GetProperty("Daily")!.GetValue(analyticsState)!).Count==1&&((IList)analyticsState.GetType().GetProperty("Hourly")!.GetValue(analyticsState)!).Count==1,"Analytics duplicate/invalid rows were retained");
    Check(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(analyticsPath)!,"recovery")).Any(file=>File.ReadAllText(file)==analyticsOriginal),"Analytics recovery did not preserve original");
    Call(analytics,"OnWorldSave");
    using(var saved=JsonDocument.Parse(File.ReadAllText(analyticsPath)))Check(saved.RootElement.GetProperty("daily").GetArrayLength()>=1,"Analytics writer did not flush valid state");
    Console.WriteLine("PASS A7: analytics duplicate/invalid row recovery preserves the original and persists a readable snapshot.");


    var incidentConfig = Activator.CreateInstance(M("AntiCheat.AntiCheatConfig"))!;
    incidentConfig.GetType().GetProperty("HistoryEnabled")!.SetValue(incidentConfig, true);
    var history = NewService(M("AntiCheat.AntiCheatHistoryStore"), fixture, incidentConfig); disposables.Add((IDisposable)history);
    string historyPath = (string)Field(history, "statePath");
    File.WriteAllText(historyPath, "{\"schemaVersion\":1,\"nextId\":12,\"incidents\":[{\"id\":11,\"unixMilliseconds\":1791450000000,\"code\":\"old-incident\"},null]}");
    Call(history, "Load");
    Check((int)history.GetType().GetProperty("Count")!.GetValue(history)! == 1, "Null incident row discarded valid history");
    string historyOriginal = (string)history.GetType().GetProperty("RecoveryFile")!.GetValue(history)!;
    Check(File.ReadAllText(historyOriginal).Contains("old-incident"), "Incident original recovery copy is missing");
    var record = history.GetType().GetMethods().Single(m => m.Name == "Record" && m.IsPublic);
    record.Invoke(history, new object[] {player, "audit-incident", 1, "Local fixture", "fixture", "fixture", 0, 0, "clear", "none"});
    Call(history, "OnWorldSave");
    string savedHistory = File.ReadAllText(historyPath);
    // The old fixture needs a recent timestamp so normal retention does not expire it.
    Check(savedHistory.Contains("audit-incident"), "Incident writer did not flush");
    Console.WriteLine("PASS H2: null incident row salvages valid records and preserves the complete original before any replacement.");

    string log = Path.Combine(temporary, "burst.log");
    var logType = M("Services.IncrementalLogReader");
    object reader = Activator.CreateInstance(logType, true)!;
    string prefix = string.Concat(Enumerable.Repeat("ordinary log line\n", 16_000));
    File.WriteAllText(log, prefix + "[Error] exception at boundary\n    at Local.Test()\n" + new string('z', 1_000_000) + "\nUTF8 🐉 中文 final\n", new UTF8Encoding(false));
    string ReadAll(object active, string file, long offset = 0) {
        var output = new StringBuilder();
        int passes = 0;
        do {
            long before = offset;
            output.Append(Call(active, "Read", file, offset, (Func<string, bool>)(line => line.Contains("[Error]"))));
            offset = (long)logType.GetProperty("Offset")!.GetValue(active)!;
            Check(offset - before <= 256 * 1024, "Log read exceeded its per-pass budget");
            Check(++passes < 100, "Log reader made no progress");
        } while ((long)logType.GetProperty("BacklogBytes")!.GetValue(active)! > 0);
        return output.ToString();
    }
    string parsed = ReadAll(reader, log);
    Check(parsed.Contains("[Error] exception at boundary\n    at Local.Test()") && parsed.Contains("UTF8 🐉 中文 final") && !parsed.Contains(new string('z',100)), "Log context/UTF8/oversized-line handling failed");
    Check((long)logType.GetProperty("OversizedLines")!.GetValue(reader)! == 1, "Oversized log line not accounted for");
    string utf8 = Path.Combine(temporary, "utf8.log");
    File.WriteAllText(utf8, string.Concat(Enumerable.Repeat("short\n", 43690)) + "abc🐉end\n", new UTF8Encoding(false));
    Check(ReadAll(Activator.CreateInstance(logType,true)!,utf8).EndsWith("abc🐉end\n"), "Split UTF8 code point was lost");
    long oldOffset = (long)logType.GetProperty("Offset")!.GetValue(reader)!;
    File.WriteAllText(log, "rotated first\n" + string.Concat(Enumerable.Repeat("replacement line\n", 80_000)), new UTF8Encoding(false));
    Check(ReadAll(reader, log, oldOffset).StartsWith("rotated first\n"), "Same-path larger rotation was not detected");
    Console.WriteLine("PASS A2: 256 KiB incremental budgets, giant-line rejection, UTF8 carry, exception context and same-path rotation.");

    string zipPath = Path.Combine(temporary, "metadata.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create)) {
        using var metadataWriter = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        metadataWriter.Write("{\"modid\":\"auditfixture\",\"version\":\"1.0.0\",\"dependencies\":{\"game\":\"99.0.0\",\"xskills\":\"99.0.0\"}}");
    }
    var validate = M("ModUpdates.ModUpdateCheckerService").GetMethod("ValidateDownloadedMod", BindingFlags.Static | BindingFlags.NonPublic)!;
    var validated = ((bool Success, string Error, Dictionary<string,string> Dependencies))validate.Invoke(null, new object[] {zipPath, "auditfixture", "1.0.0"})!;
    Check(validated.Success && validated.Dependencies["xskills"] == "99.0.0", "Dependency minimum version was discarded");
    var compatibility = M("ModUpdates.ModUpdateCheckerService").GetMethod("ValidateDependencyVersions",BindingFlags.Static|BindingFlags.NonPublic)!;
    var versions = new Dictionary<string,string> { ["game"]="1.22.7",["xskills"]="1.0.0" };
    var issues = (IList)compatibility.Invoke(null,new object[] {validated.Dependencies,versions})!;
    Check(issues.Count==2,"Incompatible dependency/game versions were accepted");
    Check(((IList)compatibility.Invoke(null,new object[] {new Dictionary<string,string> { ["xskills"]="1.0.0-rc.1",["game"]="*" },versions})!).Count==0,"Valid minimum/prerelease dependency failed");
    Check(((IList)compatibility.Invoke(null,new object[] {new Dictionary<string,string> { ["missing"]="" },versions})!).Count==1,"Unversioned missing dependency accepted");
    Console.WriteLine("PASS A3: minimum versions retained, incompatible game/mod versions and missing dependencies rejected, valid stable/prerelease and wildcard dependencies accepted. No mod installation attempted.");
    foreach(string badPath in new[]{"../modinfo.json","/absolute.txt","./modinfo.json","MODINFO.JSON"}) {
        string unsafeZip=Path.Combine(temporary,Guid.NewGuid()+".zip");
        using(var archive=System.IO.Compression.ZipFile.Open(unsafeZip,System.IO.Compression.ZipArchiveMode.Create)) {
            using(var safeMetadata=new StreamWriter(archive.CreateEntry("modinfo.json").Open()))safeMetadata.Write("{\"modid\":\"auditfixture\",\"version\":\"1.0.0\"}");
            using(var unsafeFile=new StreamWriter(archive.CreateEntry(badPath).Open()))unsafeFile.Write("unsafe fixture");
        }
        var rejected=((bool Success,string Error,Dictionary<string,string> Dependencies))validate.Invoke(null,new object[]{unsafeZip,"auditfixture","1.0.0"})!;
        Check(!rejected.Success,"Unsafe/duplicate ZIP path was accepted: "+badPath);
    }
    Console.WriteLine("PASS: update staging rejects parent/absolute/aliased paths and duplicate root metadata.");


    var writerType = suite.GetType("Newfies.Storage.DeferredJsonFileWriter",true)!;
    string blockedPath = Path.Combine(temporary,"writer-destination");Directory.CreateDirectory(blockedPath);
    object writer = Activator.CreateInstance(writerType,blockedPath,(Action<string>)(_=>{}))!;disposables.Add((IDisposable)writer);
    Call(writer,"Queue",(Func<byte[]>)(()=>Encoding.UTF8.GetBytes("{\"generation\":1}")));
    Check(!(bool)Call(writer,"Flush",5000)!,"Injected writer failure reported success");
    Directory.Delete(blockedPath);
    Call(writer,"Queue",(Func<byte[]>)(()=>Encoding.UTF8.GetBytes("{\"generation\":2}")));
    // Flush reports a previous error immediately; wait for the worker's signalled retry.
    Check(SpinWait.SpinUntil(()=>File.Exists(blockedPath) && (string)writerType.GetProperty("LastError")!.GetValue(writer)! == "",5000),"Background retry failed");
    Check((bool)Call(writer,"Flush",5000)! && File.ReadAllText(blockedPath).Contains("2"),"Retry lost the newer queued generation");
    Console.WriteLine("PASS A7: failed atomic write retains the pending snapshot; a newer generation supersedes it and persists after recovery.");
    Console.WriteLine("All audit safety regressions passed using temporary files and injected failures; no live world, database, installation or messages used.");
} finally { foreach (var service in disposables.AsEnumerable().Reverse()) service.Dispose(); Directory.Delete(temporary, true); }

public class AuditProxy : DispatchProxy {
    public AuditFixture Fixture = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        switch (method!.Name) {
            case "GetOrCreateDataPath": return Fixture.Root;
            case "get_SavegameIdentifier": return "audit-world";
            case "GetData": if (Fixture.ThrowReads) throw new IOException("Injected audit read failure"); return Fixture.SaveBytes;
            case "StoreData": if (Fixture.ThrowWrites) throw new IOException("Injected audit write failure"); Fixture.SaveBytes = (byte[])args![1]!; return null;
            case "Error": Fixture.Errors++; return null;
            case "Warning": Fixture.Warnings++; return null;
            case "LoadModConfig": return null;
        }
        return Fixture.Make(method.ReturnType);
    }
}

public class AuditFixture(string root) {
    public string Root = root;
    public byte[]? SaveBytes;
    public bool ThrowReads, ThrowWrites;
    public int Errors, Warnings;
    readonly Dictionary<Type, object> proxies = new();
    public object? Make(Type type) {
        if (type == typeof(void)) return null;
        if (type == typeof(string)) return "";
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (!type.IsInterface) return type.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(type) : null;
        if (proxies.TryGetValue(type, out var cached)) return cached;
        object proxy = DispatchProxy.Create(type, typeof(AuditProxy));
        ((AuditProxy)proxy).Fixture = this;
        proxies[type] = proxy;
        return proxy;
    }
}
