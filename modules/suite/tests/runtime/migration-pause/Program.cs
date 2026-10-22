using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
string game=Path.GetFullPath(Environment.GetEnvironmentVariable("VINTAGE_STORY")!);
string path=Path.GetFullPath(Environment.GetEnvironmentVariable("NSS_ASSEMBLY")!);
AssemblyLoadContext.Default.Resolving+=(c,n)=>{foreach(string r in new[]{Path.GetDirectoryName(path)!,game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){string f=Path.Combine(r,n.Name+".dll");if(File.Exists(f))return c.LoadFromAssemblyPath(f);}return null;};
var asm=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);var type=asm.GetType("BlockLogger.WorldRepair.BlockIdRepairService")!;
const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static;
object service=RuntimeHelpers.GetUninitializedObject(type);FieldInfo Field(string n)=>type.GetField(n,flags)!;
void Set(string n,object? v)=>Field(n).SetValue(service,v);object Get(string n)=>Field(n).GetValue(service)!;
object? Call(string n,params object?[] args)=>type.GetMethod(n,flags)!.Invoke(service,args);
int count=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);count++;}
foreach(var f in type.GetFields(flags).Where(f=>!f.IsStatic)){
 if(f.FieldType==typeof(object))f.SetValue(service,new object());
 else if(f.FieldType.Namespace=="System.Collections.Generic"&&f.FieldType.GetConstructor(Type.EmptyTypes)!=null)f.SetValue(service,Activator.CreateInstance(f.FieldType));
 else if(f.Name is "loadedMigration" or "migrationSkips")f.SetValue(service,Activator.CreateInstance(f.FieldType));
}
var tasks=new ConcurrentQueue<Action>();int worldReads=0,unregisters=0;
object? Default(Type t)=>t==typeof(void)?null:t.IsValueType?Activator.CreateInstance(t):null;
object Proxy(Type t,Func<MethodInfo,object?[]?,object?> handler){var p=DispatchProxy.Create(t,typeof(ApiProxy));((ApiProxy)p).Handler=handler;return p;}
var apiType=Field("api").FieldType;var api=Proxy(apiType,(m,a)=>{
 if(m.Name=="get_World"){worldReads++;throw new Exception("Paused migrator accessed world");}
 if(m.Name=="get_Event")return Proxy(m.ReturnType,(em,ea)=>{if(em.Name=="UnregisterGameTickListener")unregisters++;if(em.Name=="EnqueueMainThreadTask")tasks.Enqueue((Action)ea![0]!);return Default(em.ReturnType);});
 return Default(m.ReturnType);
});Set("api",api);
string directory=Path.Combine(Path.GetTempPath(),"nss-pause-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);Set("directory",directory);
try{
 var settings=Get("loadedMigration");var plan=Activator.CreateInstance(type.GetNestedType("Plan")!)!;settings.GetType().GetProperty("Plan")!.SetValue(settings,plan);settings.GetType().GetProperty("Enabled")!.SetValue(settings,true);
 Set("loadedActive",true);Set("loadedOwner",service);Set("liveSweepTick",55L);((IDictionary)Get("loadedReplacements")).Add(123,456);((IDictionary)Get("loadedPlainReplacements")).Add(123,456);Get("liveChunkKeys").GetType().GetMethod("Enqueue")!.Invoke(Get("liveChunkKeys"),[100L]);
 using var backup=new CancellationTokenSource();var backupToken=backup.Token;Set("liveActivationCancel",backup);
 using var unrelated=new CancellationTokenSource();using var preparation=new CancellationTokenSource();Set("cancel",unrelated);Set("migrationInventoryCancel",preparation);
 Call("DisableLoadedMigration");Check(backupToken.IsCancellationRequested,"Pause left backup activation running");Check(preparation.IsCancellationRequested,"Pause left inventory preparation running");Check(!unrelated.IsCancellationRequested,"Pause cancelled an unrelated scan");Check(!(bool)Get("loadedActive"),"Loaded hook remained active");Check(Field("loadedOwner").GetValue(null)==null,"Paused hook kept its owner");Check((long)Get("liveSweepTick")==0&&unregisters==1,"Sweep listener not removed");Check(((ICollection)Get("liveChunkKeys")).Count==0,"Sweep retained queued terrain");Check(((IDictionary)Get("loadedReplacements")).Count==0&&((IDictionary)Get("loadedPlainReplacements")).Count==0,"Runtime maps retained");Check(ReferenceEquals(settings.GetType().GetProperty("Plan")!.GetValue(settings),plan),"Saved rules discarded");Check(!JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"loaded-chunk-migration.json"))).RootElement.GetProperty("Enabled").GetBoolean(),"Pause not persisted");
 var prefix=type.GetMethod("BeforeLoadedChunk",flags)!;var loadedGate=Get("loadedGate");
 // Even with the old lock held by another thread and an invalid payload, the
 // paused prefix must return without world reads, parsing or lock contention.
 foreach(bool staleOwner in new[]{false,true}){if(staleOwner)Set("loadedOwner",service);Monitor.Enter(loadedGate);try{var run=Task.Run(()=>{for(int i=0;i<1000;i++){object?[] a=[new byte[]{255},null,null];prefix.Invoke(null,a);if(a[2]!=null)throw new Exception("Paused prefix produced a change");}});Check(run.Wait(2000),"Paused chunk hook waited for migration lock");run.GetAwaiter().GetResult();}finally{Monitor.Exit(loadedGate);}}
 Check(worldReads==0&&(long)Get("loadedChecked")==0,"Paused hook inspected chunks");Set("loadedOwner",null);
 var pending=new TaskCompletionSource();Set("worker",pending.Task);settings.GetType().GetProperty("Enabled")!.SetValue(settings,true);Call("StartOrQueueMigrationInventoryPreparation",plan);Call("StartOrQueueMigrationInventoryPreparation",plan);Check((string)settings.GetType().GetProperty("State")!.GetValue(settings)! =="preparation-queued","Resume did not wait for cancelled worker");Call("DisableLoadedMigration");pending.SetResult();Check(SpinWait.SpinUntil(()=>!tasks.IsEmpty,2000),"Deferred resume was not scheduled");while(tasks.TryDequeue(out var task))task();Check(!(bool)settings.GetType().GetProperty("Enabled")!.GetValue(settings)!&&worldReads==0,"Late preparation resumed a paused migration");
 using var cancelledPage=new CancellationTokenSource();cancelledPage.Cancel();try{Call("Page","/missing-world.db",null,cancelledPage.Token);throw new Exception("Cancelled page opened world storage");}catch(TargetInvocationException e){Check(e.InnerException is OperationCanceledException,"Cancelled page was not stopped before database access");}
 Console.WriteLine($"PASS {count} shipping pause checks: owned cancellation, independent scans, hook fast path, sweep removal, released maps, saved rules and stale resume.");
}finally{Set("loadedOwner",null);Directory.Delete(directory,true);}
public class ApiProxy:DispatchProxy{public Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);}
