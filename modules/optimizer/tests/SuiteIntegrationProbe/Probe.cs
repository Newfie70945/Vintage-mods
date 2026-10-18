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
  api.ChatCommands.Create("nosbridgetest").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>{
   var checks=new List<string>();
   try {
    var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("BlockLogger.Integrations.OptimizerControlBridge")).First(x=>x!=null)!;
    var request=type.GetMethod("Request",BindingFlags.Public|BindingFlags.Static)!;
    JsonElement Call(string op,string settings="{}")=>JsonSerializer.SerializeToElement(request.Invoke(null,[api,op,settings]));
    void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
    var status=Call("get");Check(status.GetProperty("available").GetBoolean(),"Suite finds NOS via event bridge");
    var original=status.GetProperty("optimizer").GetProperty("settings").GetRawText();
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
