using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
[assembly:ModInfo("NOS Suite integration test fixture", "nossuiteintegrationprobe",Version="0.0.0",Side="Server")]
public sealed class Probe:ModSystem
{
 public override bool ShouldLoad(EnumAppSide side)=>side==EnumAppSide.Server;
 public override void StartServerSide(ICoreServerAPI api)
 {
  object Field(object owner,string name) { for(var t=owner.GetType();t!=null;t=t.BaseType){var f=t.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);if(f!=null)return f.GetValue(owner)!;}throw new Exception("Missing field "+name); }
  api.ChatCommands.Create("nosgenerate").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   var spawn=api.World.DefaultSpawnPosition;int x=(int)(spawn.X/32)+100,z=(int)(spawn.Z/32)+100,done=0;
   for(int dx=0;dx<2;dx++)for(int dz=0;dz<2;dz++)api.WorldManager.LoadChunkColumnPriority(x+dx,z+dz,new ChunkLoadOptions{KeepLoaded=true,OnLoaded=()=>{
    if(System.Threading.Interlocked.Increment(ref done)==4)api.Logger.Notification("NOS_WORLDGEN_PASS columns=4");
   }});
   return TextCommandResult.Success("NOS_WORLDGEN_QUEUED columns=4");
  });
  api.ChatCommands.Create("nosstoragetoggle").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   try{
    var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BlockLogger.Integrations.OptimizerControlBridge")).First(x=>x!=null)!;
    var request=type.GetMethod("Request",BindingFlags.Public|BindingFlags.Static)!;
    var state=JsonSerializer.SerializeToElement(request.Invoke(null,[api,"get","{}"]));
    bool enabled=state.GetProperty("optimizer").GetProperty("settings").GetProperty("saveSnapshotRelease").GetBoolean();
    var result=JsonSerializer.SerializeToElement(request.Invoke(null,[api,"set",JsonSerializer.Serialize(new{saveSnapshotRelease=!enabled})]));
    if(!result.GetProperty("success").GetBoolean())throw new Exception("Toggle failed");
    api.Logger.Notification("NOS_STORAGE_TOGGLE enabled="+!enabled);return TextCommandResult.Success("NOS_STORAGE_TOGGLE");
   }catch(Exception e){api.Logger.Error("NOS_STORAGE_FAIL "+e);return TextCommandResult.Error(e.Message);}
  });
  api.ChatCommands.Create("nosstoragestock").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   try{
    var thread=Field(api.World,"chunkThread");var save=Field(thread,"loadsavegame");
    bool pending=(bool)Field(thread,"runOffThreadSaveNow");int count=((System.Collections.IDictionary)Field(save,"chunksCopy")).Count;
    if(pending||count<=0)throw new Exception($"Stock snapshot expected; pending={pending}, count={count}");
    api.Logger.Notification("NOS_STORAGE_STOCK_PASS snapshot="+count);return TextCommandResult.Success("NOS_STORAGE_STOCK_PASS");
   }catch(Exception e){api.Logger.Error("NOS_STORAGE_FAIL "+e);return TextCommandResult.Error(e.Message);}
  });
  api.ChatCommands.Create("nosstoragecheck").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   try {
    var thread=Field(api.World,"chunkThread");var save=Field(thread,"loadsavegame");
    bool pending=(bool)Field(thread,"runOffThreadSaveNow");var snapshot=(System.Collections.IDictionary)Field(save,"chunksCopy");
    var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("NewfiesOptimizer.Optimizations.SaveSnapshotOptimizations")).First(x=>x!=null)!;
    var counts=type.GetProperty("Counters")!.GetValue(null)!;
    long released=(long)counts.GetType().GetField("Item1")!.GetValue(counts)!,completed=(long)counts.GetType().GetField("Item2")!.GetValue(counts)!;
    if(pending||snapshot.Count!=0||released<=0||completed<=0)throw new Exception($"pending={pending}, snapshot={snapshot.Count}, released={released}, completed={completed}");
    api.Logger.Notification($"NOS_STORAGE_PASS snapshot={snapshot.Count}, released={released}, completed={completed}");return TextCommandResult.Success("NOS_STORAGE_PASS");
   }catch(Exception e){api.Logger.Error("NOS_STORAGE_FAIL "+e);return TextCommandResult.Error(e.Message);}
  });
  api.ChatCommands.Create("nosbridgetest").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   var checks=new List<string>();
   try {
    var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BlockLogger.Integrations.OptimizerControlBridge")).First(x=>x!=null)!;
    var request=type.GetMethod("Request",BindingFlags.Public|BindingFlags.Static)!;
    JsonElement Call(string op,string settings="{}")=>JsonSerializer.SerializeToElement(request.Invoke(null,[api,op,settings]));
    void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
    var status=Call("get");Check(status.GetProperty("available").GetBoolean(),"Suite finds NOS via event bridge");
    var original=status.GetProperty("optimizer").GetProperty("settings").GetRawText();
    var aiOff=Call("set","{\"aiFilterDispatch\":false}");
    Check(aiOff.GetProperty("optimizer").GetProperty("modules").EnumerateArray().Any(x=>x.GetProperty("name").GetString()=="aiFilterDispatch"&&x.GetProperty("status").GetString()=="disabled"),"AI switch independently disables its patch");
    Check(aiOff.GetProperty("optimizer").GetProperty("modules").EnumerateArray().Any(x=>x.GetProperty("name").GetString()=="repulseDelegateReuse"&&x.GetProperty("status").GetString()=="active"),"repulsion remains active with profiling off");
    var repulseOff=Call("set","{\"aiFilterDispatch\":true,\"repulseDelegateReuse\":false}");
    Check(repulseOff.GetProperty("optimizer").GetProperty("modules").EnumerateArray().Any(x=>x.GetProperty("name").GetString()=="repulseDelegateReuse"&&x.GetProperty("status").GetString()=="disabled"),"repulsion switch independently disables its patch");
    Check(repulseOff.GetProperty("optimizer").GetProperty("modules").EnumerateArray().Any(x=>x.GetProperty("name").GetString()=="aiFilterDispatch"&&x.GetProperty("status").GetString()=="active"),"AI remains active with profiling off");
    var storageOff=Call("set","{\"worldgenRequestReuse\":false,\"saveSnapshotRelease\":false}");
    Check(storageOff.GetProperty("optimizer").GetProperty("modules").EnumerateArray().Where(x=>x.GetProperty("name").GetString() is "worldgenRequestReuse" or "saveSnapshotRelease").All(x=>x.GetProperty("status").GetString()=="disabled"),"storage switches independently disable patches");
    Check(storageOff.GetProperty("optimizer").GetProperty("modules").EnumerateArray().Any(x=>x.GetProperty("name").GetString()=="aiFilterDispatch"&&x.GetProperty("status").GetString()=="active"),"storage switches leave AI optimization active");
    var off=Call("set","{\"enabled\":false}");Check(off.GetProperty("success").GetBoolean(),"master-off saves and applies");
    Check(off.GetProperty("optimizer").GetProperty("modules").EnumerateArray().All(x=>x.GetProperty("status").GetString() is "blocked" or "disabled"),"master-off retains stock methods");
    var invalid=Call("set","{\"assetCacheMaxMiB\":5000}");Check(!invalid.GetProperty("success").GetBoolean(),"invalid bound rejected");
    Check(!Call("set","{\"unrecognized\":true}").GetProperty("success").GetBoolean(),"unknown key rejected");
    Check(Call("set","{\"enabled\":true,\"safeMode\":true}").GetProperty("success").GetBoolean(),"safe mode applies");
    Check(Call("get").GetProperty("optimizer").GetProperty("modules").EnumerateArray().All(x=>x.GetProperty("status").GetString() is "blocked" or "disabled"),"safe mode retains stock methods");
    Check(Call("set",original).GetProperty("success").GetBoolean(),"original settings restored");
    var start=Call("profile-start");Check(start.GetProperty("success").GetBoolean(),"capture starts through Suite bridge");
    Check(!Call("set","{\"enabled\":false}").GetProperty("success").GetBoolean(),"active capture locks edits");
    Call("profile-stop");Check(!Call("get").GetProperty("optimizer").GetProperty("profiling").GetBoolean(),"capture stops through Suite bridge");
    var cfg=api.LoadModConfig<Dictionary<string,object>>("optimizer.json")!;
    Check(Convert.ToBoolean(cfg["Enabled"]),"restored configuration persisted");
    api.Logger.Notification("NOS_BRIDGE_PASS "+JsonSerializer.Serialize(checks));
    return TextCommandResult.Success("NOS_BRIDGE_PASS: "+checks.Count+" checks");
   } catch(Exception e){api.Logger.Error("NOS_BRIDGE_FAIL "+e);return TextCommandResult.Error("NOS_BRIDGE_FAIL "+e.Message);}
  });
 }
}
