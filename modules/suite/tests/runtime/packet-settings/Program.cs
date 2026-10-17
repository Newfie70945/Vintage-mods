using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")??throw new Exception("Set VINTAGE_STORY");
string dll=Environment.GetEnvironmentVariable("NSS_ASSEMBLY")??throw new Exception("Set NSS_ASSEMBLY");
var roots=new[]{game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetDirectoryName(dll)!};
AssemblyLoadContext.Default.Resolving+=(context,name)=>{foreach(var root in roots){string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;};
var mod=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
Type T(string name)=>mod.GetType("BlockLogger.AntiCheat."+name,true)!;
const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
object config=Activator.CreateInstance(T("AntiCheatConfig"))!;
config.GetType().GetProperty("Enabled")!.SetValue(config,false);
object service=RuntimeHelpers.GetUninitializedObject(T("AntiCheatService"));
service.GetType().GetField("config",flags)!.SetValue(service,config);
var apiField=service.GetType().GetField("api",flags)!;
int stores=0;bool failStore=false;
string temp=Path.Combine(Path.GetTempPath(),"nss-packet-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
object logger=Proxy(apiField.FieldType.GetInterfaces().SelectMany(x=>x.GetProperties()).First(x=>x.Name=="Logger").PropertyType,(m,a)=>m.ReturnType==typeof(bool)?false:null);
object api=Proxy(apiField.FieldType,(m,a)=>m.Name switch{
 "GetOrCreateDataPath"=>temp,
 "StoreModConfig"=>Store(),
 "get_Logger"=>logger,
 _=>throw new Exception("Unexpected API access "+m.Name)
});
object? Store(){stores++;if(failStore)throw new IOException("fixture store failure");return null;}
apiField.SetValue(service,api);
var save=service.GetType().GetMethod("SavePacketGuardSettings")!;
var requestType=T("AntiCheatPacketGuardSaveRequest");
object Request(){var r=Activator.CreateInstance(requestType)!;requestType.GetProperty("Enabled")!.SetValue(r,false);return r;}
object Property(object obj,string name)=>obj.GetType().GetProperty(name)!.GetValue(obj)!;
void Set(object obj,string name,object value)=>obj.GetType().GetProperty(name)!.SetValue(obj,value);
bool Save(object request){var result=save.Invoke(service,new[]{request})!;return(bool)result.GetType().GetField("Item1")!.GetValue(result)!;}
var names=new List<string>{"IngressEnabled","ConfirmedSpeedRepeatRisk","KickRepeatedConfirmedSpeed","MovementModeLimitsEnabled","MaxHorizontalSpeed","GroundMaxHorizontalSpeed","SwimmingMaxHorizontalSpeed","AirborneMaxHorizontalSpeed","MountProfilesEnabled","MountProfileEnforcementEnabled","UnknownMountEnforcementEnabled","MountProfileConfirmSamples","MountProfileConfirmWindowMs","MountRiderCorrelationEnabled"};
foreach(var prefix in new[]{"Draconis","Shipwright","UnknownMount"})foreach(var suffix in new[]{"HorizontalSpeed","VerticalSpeed","Acceleration","RiderDistance","RiderRelativeSpeed"})names.Add(prefix+"Max"+suffix);
try{
 object request=Request();
 foreach(string name in names){var type=Nullable.GetUnderlyingType(requestType.GetProperty(name)!.PropertyType)!;object value=type==typeof(bool)?!(bool)Property(config,"PacketGuard"+name):type==typeof(int)?(object)((int)Property(config,"PacketGuard"+name)+1):(double)Property(config,"PacketGuard"+name)+1;Set(request,name,value);}
 if(!Save(request)||stores!=1)throw new Exception("Settings did not persist");
 object snapshot=service.GetType().GetMethod("BuildPacketGuardSnapshot",flags)!.Invoke(service,null)!;
 foreach(string name in names){if(!Equals(Property(request,name),Property(config,"PacketGuard"+name))||!Equals(Property(request,name),Property(snapshot,name)))throw new Exception("Round trip failed "+name);}
 if(!Save(Request()))throw new Exception("Legacy request failed");
 foreach(string name in names)if(!Equals(Property(request,name),Property(config,"PacketGuard"+name)))throw new Exception("Legacy request reset "+name);
 var gui=service.GetType().GetMethod("SaveGuiPacketProtectionSettings")!;
 gui.Invoke(service,new[]{Activator.CreateInstance(gui.GetParameters()[0].ParameterType)});
 foreach(string name in names)if(!Equals(Property(request,name),Property(config,"PacketGuard"+name)))throw new Exception("Shift+L request reset "+name);
 object on=Request();Set(on,"IngressEnabled",true);if(!Save(on)||!(bool)Property(config,"PacketGuardIngressEnabled"))throw new Exception("Ingress could not be re-enabled");
 if(!Save(Request())||!(bool)Property(config,"PacketGuardIngressEnabled"))throw new Exception("Legacy request reset enabled ingress");
 object off=Request();Set(off,"IngressEnabled",false);if(!Save(off)||(bool)Property(config,"PacketGuardIngressEnabled"))throw new Exception("Ingress could not be disabled again");
 object bounds=Request();Set(bounds,"DraconisMaxHorizontalSpeed",999d);if(!Save(bounds)||(double)Property(config,"PacketGuardDraconisMaxHorizontalSpeed")!=300d)throw new Exception("Configured bounds not applied");
 object invalid=Request();Set(invalid,"GroundMaxHorizontalSpeed",double.NaN);int before=stores;if(Save(invalid)||stores!=before)throw new Exception("Non-finite input reached persistence");
 object failing=Request();Set(failing,"IngressEnabled",true);Set(failing,"DraconisMaxHorizontalSpeed",125d);failStore=true;if(Save(failing)||(bool)Property(config,"PacketGuardIngressEnabled")||(double)Property(config,"PacketGuardDraconisMaxHorizontalSpeed")!=300d)throw new Exception("Failed save did not restore prior values");
 Console.WriteLine("PASS: 29 production settings save/config/snapshot round-trip; legacy and Shift+L saves preserve new fields; bounds, non-finite rejection and persistence-failure rollback. No game world created.");
}finally{Directory.Delete(temp,true);}
static object Proxy(Type type,Func<MethodInfo,object?[]?,object?> handler){object result=DispatchProxy.Create(type,typeof(SettingsProxy));((SettingsProxy)result).Handler=handler;return result;}
public class SettingsProxy:DispatchProxy{public Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);}
