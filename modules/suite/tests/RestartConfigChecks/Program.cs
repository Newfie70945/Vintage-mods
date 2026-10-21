using System.Reflection;
using System.Runtime.Loader;
using BlockLogger.Restarts;
using Newtonsoft.Json;
var game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
AssemblyLoadContext.Default.Resolving+=(_,name)=>{
 foreach(var folder in new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetFullPath("artifacts/build/suite")}){
 var path=Path.Combine(folder,name.Name+".dll");if(File.Exists(path))return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
 }return null;
};
Checks.Run();
static class Checks
{
 internal static void Run()
 {
  int checks=0;
  void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
  var subset=JsonConvert.DeserializeObject<AutomatedRestartConfig>("{\"WarningMinutes\":[1,5,10]}")!;
  subset.Clamp();Check(subset.WarningMinutes.SequenceEqual(new[]{1,5,10}),"Custom warnings were merged with defaults: "+string.Join(',',subset.WarningMinutes));
  foreach(var source in new[]{"[]","null"}){
   var empty=JsonConvert.DeserializeObject<AutomatedRestartConfig>("{\"WarningMinutes\":"+source+"}")!;empty.Clamp();Check(empty.WarningMinutes.Count==0,"Explicit empty/null warnings must stay empty");
  }
  var missing=JsonConvert.DeserializeObject<AutomatedRestartConfig>("{}")!;missing.Clamp();Check(missing.WarningMinutes.SequenceEqual(new[]{1,2,3,4,5,10,20,30}),"Missing property keeps installation defaults");
  var dirty=JsonConvert.DeserializeObject<AutomatedRestartConfig>("{\"WarningMinutes\":[10,5,5,1,0,-1,1441]}")!;dirty.Clamp();Check(dirty.WarningMinutes.SequenceEqual(new[]{1,5,10}),"Normalization should filter/sort custom values only");
  for(int i=0;i<5;i++){subset=JsonConvert.DeserializeObject<AutomatedRestartConfig>(JsonConvert.SerializeObject(subset))!;subset.Clamp();Check(subset.WarningMinutes.SequenceEqual(new[]{1,5,10}),"Reload cycle restored defaults");}
  var populated=new AutomatedRestartConfig();JsonConvert.PopulateObject("{\"WarningMinutes\":[5],\"RestartTimesUtc\":[\"12:00\"]}",populated);populated.Clamp();
  Check(populated.WarningMinutes.SequenceEqual(new[]{5}),"Populate merged defaults");
  JsonConvert.PopulateObject("{\"WarningMinutes\":[],\"RestartTimesUtc\":[\"18:00\"]}",populated);populated.Clamp();
  Check(populated.WarningMinutes.Count==0&&populated.RestartTimesUtc.SequenceEqual(new[]{"18:00:00"}),"Populate retained deleted warnings/schedule entries");
  Console.WriteLine($"PASS {checks} restart configuration replacement/reload checks");
 }
}
