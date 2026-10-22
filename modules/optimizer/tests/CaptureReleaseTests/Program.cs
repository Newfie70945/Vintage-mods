using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using NewfiesOptimizer;
string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods")}){var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));}return null;};
Checks.Run();
static class Checks
{
 [MethodImpl(MethodImplOptions.NoInlining)]public static void Run()
 {
  var type=typeof(RuntimeProfiler);var profiler=(RuntimeProfiler)RuntimeHelpers.GetUninitializedObject(type);
  void Set(string name,object value)=>type.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(profiler,value);
  foreach(string name in new[]{"timingCounters","blockedHookGroups"}){var field=type.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!;Set(name,Activator.CreateInstance(field.FieldType)!);}
  var session=new ProfileSession(60,8);session.Tick(5);Set("<Session>k__BackingField",session);
  if(profiler.ClearCapture().Success||!ReferenceEquals(profiler.Session,session)||session.TickCount!=1)throw new Exception("Running capture cleared");
  session.Stop();var report=session.Snapshot([],[],"1.22.7");
  if(!profiler.ClearCapture().Success||profiler.Session!=null||report.Tick.Count!=1)throw new Exception("Stopped capture not released or independent export snapshot damaged");
  if(!profiler.ClearCapture().Success)throw new Exception("Empty release is not idempotent");
  Console.WriteLine("PASS running-capture refusal, stopped-report release, independent export snapshot and idempotent cleanup; no collection requested.");
 }
}
