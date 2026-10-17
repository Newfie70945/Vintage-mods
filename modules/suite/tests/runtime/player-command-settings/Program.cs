using System.Reflection;
using System.Collections;
using System.Runtime.Loader;
using System.Linq.Expressions;
using System.Text.Json;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string dll = Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY");
AssemblyLoadContext.Default.Resolving += (context,name) => {
    foreach (string root in new[] {game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetDirectoryName(dll)!}) {
        string path=Path.Combine(root,name.Name+".dll");if(File.Exists(path))return context.LoadFromAssemblyPath(path);
    }
    return null;
};
var mod=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
string html=(string)mod.GetType("BlockLogger.WebDashboard.WebDashboardPage",true)!.GetField("Html",BindingFlags.Public|BindingFlags.Static)!.GetValue(null)!;
if(html.Contains("__NSS_PLAYER_COMMANDS_SCRIPT__"))throw new Exception("Feature script placeholder not expanded");
if(args.Length>0)File.WriteAllText(args[0],html);
var requestType=mod.GetType("BlockLogger.PlayerCommands.PlayerCommandSettingsRequest",true)!;
var serviceType=mod.GetType("BlockLogger.PlayerCommands.PlayerCommandsService",true)!;
string temporary=Path.Combine(Path.GetTempPath(),"nss-playercommands-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var fixture=new EmptyServerFixture(temporary);
try {
    var ctor=serviceType.GetConstructors().Single();
    var parameters=ctor.GetParameters();
    var providerType=parameters[1].ParameterType;
    var provider=Expression.Lambda(providerType,Expression.Default(providerType.GetMethod("Invoke")!.ReturnType)).Compile();
    var service=ctor.Invoke(new[]{fixture.Make(parameters[0].ParameterType),provider});
    object config=serviceType.GetProperty("Config")!.GetValue(service)!;
    void Set(string name,object value)=>config.GetType().GetProperty(name)!.SetValue(config,value);
    object Value(string name)=>config.GetType().GetProperty(name)!.GetValue(config)!;
    object Request(string json)=>JsonSerializer.Deserialize(json,requestType,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
    void Save(object request) {
        var result=serviceType.GetMethod("SaveSettings")!.Invoke(service,new[]{request})!;
        if(!(bool)result.GetType().GetProperty("Success")!.GetValue(result)!)throw new Exception("Production save failed");
    }
    Set("WarpDefaultCooldownSeconds",91);Set("WarpDefaultWarmupSeconds",17);Set("WarpDefaultCancelOnMove",false);
    ((IDictionary)Value("HomeRoleLimits"))["supporter"]=9;
    var omitted=Request("{\"spawnCooldownSeconds\":77}");
    foreach(string name in new[]{"WarpDefaultCooldownSeconds","WarpDefaultWarmupSeconds","WarpDefaultCancelOnMove"})
        if(requestType.GetProperty(name)!.GetValue(omitted)!=null)throw new Exception("Omitted field became a value: "+name);
    if(requestType.GetProperty("HomeRoleLimits")!.GetValue(omitted)!=null||requestType.GetProperty("TeleportRolePolicies")!.GetValue(omitted)!=null)throw new Exception("Omitted shared maps became replacement values");
    Save(omitted);
    if(!((IDictionary)Value("HomeRoleLimits")).Contains("supporter"))throw new Exception("Omitted settings removed shared role limits");
    if((int)Value("SpawnCooldownSeconds")!=77||(int)Value("WarpDefaultCooldownSeconds")!=91||(int)Value("WarpDefaultWarmupSeconds")!=17||(bool)Value("WarpDefaultCancelOnMove"))throw new Exception("Unrelated settings save replaced warp defaults");
    Save(Request("{\"warpDefaultCooldownSeconds\":0,\"warpDefaultWarmupSeconds\":0,\"warpDefaultCancelOnMove\":true}"));
    if((int)Value("WarpDefaultCooldownSeconds")!=0||(int)Value("WarpDefaultWarmupSeconds")!=0||!(bool)Value("WarpDefaultCancelOnMove"))throw new Exception("Legacy explicit warp settings did not round trip");
    Save(Request("{\"warpDefaultCancelOnMove\":false}"));
    if((bool)Value("WarpDefaultCancelOnMove"))throw new Exception("Legacy explicit false warp setting did not apply");
    if(fixture.Stores<3)throw new Exception("Settings were not persisted through the production storage API");
    Console.WriteLine("PASS: compiled dashboard script expands; production Player Commands saves preserve omitted warp defaults and accept explicit legacy zero/boolean values with persisted snapshots. No game world started.");
} finally {Directory.Delete(temporary,true);}

public class EmptyApiProxy : DispatchProxy {
    public EmptyServerFixture Fixture=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args) {
        if(method!.Name=="GetOrCreateDataPath")return Fixture.Root;
        if(method.Name=="StoreModConfig"){Fixture.Stores++;return null;}
        if(method.Name=="LoadModConfig"||method.Name=="GetData"||method.Name=="Get")return null;
        return Fixture.Make(method.ReturnType);
    }
}
public class EmptyServerFixture(string root) {
    public string Root=root;public int Stores;
    readonly Dictionary<Type,object> proxies=new();
    public object? Make(Type type) {
        if(type==typeof(void))return null;
        if(type==typeof(string))return "";
        if(type.IsArray)return Array.CreateInstance(type.GetElementType()!,0);
        if(type.IsValueType)return Activator.CreateInstance(type);
        if(type.IsGenericType) {
            var def=type.GetGenericTypeDefinition();
            if(def==typeof(IEnumerable<>)||def==typeof(IList<>)||def==typeof(IReadOnlyList<>)||def==typeof(ICollection<>))return Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()));
            if(!type.IsInterface&&type.GetConstructor(Type.EmptyTypes)!=null)return Activator.CreateInstance(type);
        }
        if(!type.IsInterface)return type.GetConstructor(Type.EmptyTypes)!=null?Activator.CreateInstance(type):null;
        if(proxies.TryGetValue(type,out var cached))return cached;
        var proxy=DispatchProxy.Create(type,typeof(EmptyApiProxy));((EmptyApiProxy)proxy).Fixture=this;proxies[type]=proxy;return proxy;
    }
}
