using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

const BindingFlags members=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")??throw new Exception("Set VINTAGE_STORY");
string guardDll=Environment.GetEnvironmentVariable("CHATGUARD_ASSEMBLY")??throw new Exception("Set CHATGUARD_ASSEMBLY");
string motdDll=Environment.GetEnvironmentVariable("MOTD_ASSEMBLY")??throw new Exception("Set MOTD_ASSEMBLY");
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(string root in new[]{game,Path.Combine(game,"Lib"),Path.GetDirectoryName(guardDll)!,Path.GetDirectoryName(motdDll)!}){
  string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);
 }return null;
};
var guard=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(guardDll));
var motd=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(motdDll));
object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethods(members).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);
object Field(object target,string name)=>target.GetType().GetField(name,members)!.GetValue(target)!;
void Check(bool success,string reason){if(!success)throw new Exception(reason);}
var services=new List<IDisposable>();
string temporary=Path.Combine(Path.GetTempPath(),"nss-companion-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
try {
 var fixture=new AuditFixture(temporary);
 var guardType=guard.GetType("NewfiesChatGuard.ChatGuardService",true)!;
 object NewGuard(){var service=Activator.CreateInstance(guardType,fixture.Make(guardType.GetConstructors().Single().GetParameters()[0].ParameterType))!;services.Add((IDisposable)service);return service;}
 object service=NewGuard();
 Call(service,"Mute","player-1","Alice",10,"immutable reason","fixture",true,false);
 var state=Field(service,"state");
 var mute=((IDictionary)state.GetType().GetProperty("Mutes")!.GetValue(state)!)["player-1"]!;
 mute.GetType().GetProperty("Reason")!.SetValue(mute,"mutated after queue");
 object writer=Field(service,"stateWriter");Check((bool)Call(writer,"Flush",5000)!,"State writer failed");
 string statePath=(string)Field(service,"statePath");
 Check(File.ReadAllText(statePath).Contains("immutable reason")&&!File.ReadAllText(statePath).Contains("mutated after queue"),"State writer retained mutable game-owned data");
 Console.WriteLine("PASS A6: immutable moderation state snapshot persists off-thread, without observing later mutation.");
 object previousConfig=Field(service,"config");
 var treeType=guardType.GetMethod("HandleSaveRequest",members)!.GetParameters()[0].ParameterType.Assembly.GetType("Vintagestory.API.Datastructures.TreeAttribute",true)!;
 object configTree=Activator.CreateInstance(treeType)!;Call(configTree,"SetString","configJson","{\"Enabled\":false,\"Mode\":\"LogOnly\"}");
 fixture.ThrowConfigWrites=true;
 try { Call(service,"HandleSaveRequest",configTree);throw new Exception("Expected config write failure"); }
 catch(TargetInvocationException ex)when(ex.InnerException is IOException){}
 finally{fixture.ThrowConfigWrites=false;}
 Check(ReferenceEquals(previousConfig,Field(service,"config")),"Failed ChatGuard save changed active configuration");
 Call(service,"HandleSaveRequest",configTree);
 Check(!ReferenceEquals(previousConfig,Field(service,"config"))&&(bool)Call(configTree,"GetBool","success",false)!,"Successful ChatGuard save did not apply configuration");
 Console.WriteLine("PASS: ChatGuard settings commit only after storage succeeds.");
 using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
 {
  Func<byte[]> blocked=()=>{entered.Set();release.Wait();return Encoding.UTF8.GetBytes("{}");};
  Call(writer,"Queue",blocked);Check(entered.Wait(3000),"Writer fixture did not start");
  try { var watch=System.Diagnostics.Stopwatch.StartNew();Call(service,"OnWorldSave");Check(watch.ElapsedMilliseconds<1000,"Autosave waited for blocked background disk work"); }
  finally{release.Set();}
  Check((bool)Call(writer,"Flush",5000)!,"Queued save did not persist after writer resumed");
 }
 Console.WriteLine("PASS: ChatGuard autosave does not wait for a stalled writer; queued snapshot persists after recovery.");

 long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
 object oldHistory=Call(service,"GetHistory","inactive")!;Call(service,"AddSample",oldHistory,now-30_000_000,"old");
 object recentHistory=Call(service,"GetHistory","recent")!;Call(service,"AddSample",recentHistory,now,"recent");
 guardType.GetField("lastSamplePrune",members)!.SetValue(service,0L);Call(service,"PruneSamples",now);
 var samples=(IDictionary)Field(service,"samples");
 Check(!samples.Contains("inactive")&&samples.Contains("recent"),"Sample expiry lost active history or retained inactive player");
 for(int i=0;i<4100;i++){object history=Call(service,"GetHistory","bounded-"+i)!;Call(service,"AddSample",history,now,"live");}
 Check(samples.Count<=4096,"Chat histories grew beyond the UID budget");
 Console.WriteLine("PASS A6: inactive sample expiry, reconnect-window retention and 4,096-identity memory ceiling.");
 string audit=(string)Field(service,"auditPath");
 using(var fill=new FileStream(audit,FileMode.Create,FileAccess.Write))fill.SetLength(8*1024*1024);
 Call(service,"WriteAudit","player-1","Alice",0,"Fixture","Warn","Local test","hello","test");
 object auditWriter=Field(service,"auditWriter");Check((bool)Call(auditWriter,"Flush",5000)!,"Audit writer did not flush");
 Check(File.Exists(audit+".1")&&new FileInfo(audit+".1").Length==8*1024*1024,"Audit rotation discarded the old file");
 using(var document=JsonDocument.Parse(File.ReadAllText(audit)))Check(document.RootElement.GetProperty("MessagePreview").GetString()=="hello","Audit entry invalid");
 Check(!(bool)Call(auditWriter,"Queue",new string('x',70_000))!,"Oversized audit entry accepted");
 Check((long)auditWriter.GetType().GetProperty("Dropped")!.GetValue(auditWriter)! == 1,"Audit overflow not counted");
 Console.WriteLine("PASS A6: background audit flush, 8 MiB rotation preserving the prior file, oversized entry rejection and dropped-entry telemetry.");
 ((IDisposable)service).Dispose();
 File.WriteAllText(statePath,"{damaged moderation state");
 object recovered=NewGuard();Call(recovered,"OnWorldSave");
 Check(Directory.GetFiles(Path.Combine(temporary,"recovery")).Any(p=>File.ReadAllText(p)=="{damaged moderation state"),"Moderation recovery did not preserve original bytes");
 Console.WriteLine("PASS: damaged moderation state is preserved before a new state may be saved.");

 var logoType=motd.GetType("NewfiesCommunityMOTD.CachedLogoFile",true)!;
 object logo=Activator.CreateInstance(logoType,true)!;
 string path=Path.Combine(temporary,"logo.png");
 byte[] png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aCfkAAAAASUVORK5CYII=");File.WriteAllBytes(path,png);
 var first=((byte[] Bytes,string Hash))Call(logo,"Read",path)!;
 var second=((byte[] Bytes,string Hash))Call(logo,"Read",path)!;
 Check(ReferenceEquals(first.Bytes,second.Bytes)&&first.Hash.Length==64,"Unchanged logo/hash was not cached");
 File.WriteAllBytes(path,new byte[]{0x89,0x50,0x4e,0x47,0,0,0,0});
 Check(((byte[] Bytes,string Hash))Call(logo,"Read",path)! is var invalid && invalid.Bytes.Length==0,"Partial PNG signature accepted");
 using(var oversized=new FileStream(path,FileMode.Create,FileAccess.Write))oversized.SetLength(16*1024*1024);
 long before=GC.GetAllocatedBytesForCurrentThread();var rejected=((byte[] Bytes,string Hash))Call(logo,"Read",path)!;
 long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
 Check(rejected.Bytes.Length==0&&allocated<100_000,"Oversized logo was materialized before rejection");
 File.WriteAllBytes(path,png);var updated=((byte[] Bytes,string Hash))Call(logo,"Read",path)!;
 Check(updated.Hash==first.Hash&&!ReferenceEquals(updated.Bytes,first.Bytes),"Changed logo file did not invalidate cache");
 Console.WriteLine("PASS A11: full PNG signature, immutable bytes/hash cache, invalidation and pre-allocation rejection of a 16 MiB fixture.");
 var safety=motd.GetType("NewfiesCommunityMOTD.LogoPngSafety",true)!.GetMethod("IsSafe",BindingFlags.Static|BindingFlags.Public)!;
 bool Safe(byte[] bytes)=>(bool)safety.Invoke(null,[bytes])!;
 Check(Safe(png),"Valid small PNG rejected");
 byte[] bomb=(byte[])png.Clone();System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16,4),32768);System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(20,4),32768);
 Check(!Safe(bomb),"Small compressed logo with huge native dimensions accepted");
 File.WriteAllBytes(path,bomb);Check(((byte[] Bytes,string Hash))Call(logo,"Read",path)! is var rejectedPixels&&rejectedPixels.Bytes.Length==0,"Server logo cache accepted oversized dimensions");
 System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16,4),4096);System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(20,4),4096);
 Check(!Safe(bomb),"Combined decoded-pixel budget not checked");
 Check(!Safe(png[..24]),"Truncated IHDR accepted");
 Console.WriteLine("PASS: MOTD server/client shared PNG dimension and decoded-pixel limits before image decoding.");
 Console.WriteLine("Companion checks passed using temporary files; no network connection or player messages.");
}finally{foreach(var service in services.AsEnumerable().Reverse())service.Dispose();Directory.Delete(temporary,true);}

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
            case "StoreModConfig": if(Fixture.ThrowConfigWrites)throw new IOException("Injected config write failure");return null;
        }
        return Fixture.Make(method.ReturnType);
    }
}

public class AuditFixture(string root) {
    public string Root = root;
    public byte[]? SaveBytes;
    public bool ThrowReads, ThrowWrites, ThrowConfigWrites;
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
