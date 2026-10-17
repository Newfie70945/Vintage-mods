using System.Reflection;
using System.Runtime.Loader;
using System.Linq.Expressions;

string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
string dll=Environment.GetEnvironmentVariable("DISCORD_ASSEMBLY")!;
AssemblyLoadContext.Default.Resolving+=(context,name)=>{
 foreach(string root in new[]{game,Path.Combine(game,"Lib"),Path.GetDirectoryName(dll)!}){string file=Path.Combine(root,name.Name+".dll");if(File.Exists(file))return context.LoadFromAssemblyPath(file);}return null;
};
Assembly engine=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(game,"VintagestoryLib.dll"));
Assembly mod=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
var fixture=new Fixture();
Type serviceType=mod.GetType("NewfiesServerEssentials.Discord.Linking.DiscordLinkService",true)!;
var ctor=serviceType.GetConstructors().Single();object api=fixture.Make(ctor.GetParameters()[0].ParameterType)!;
fixture.Commands=Activator.CreateInstance(engine.GetType("Vintagestory.Common.ChatCommandApi",true)!,api)!;
Type configType=ctor.GetParameters()[1].ParameterType.GetGenericArguments()[0];object config=Activator.CreateInstance(configType)!;
var getConfig=Expression.Lambda(ctor.GetParameters()[1].ParameterType,Expression.Constant(config,configType)).Compile();
object service=ctor.Invoke(new[]{api,getConfig});
serviceType.GetMethod("RegisterCommands",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(service,null);
CommandChecks.Run(fixture,service,config);
Console.WriteLine("PASS: compiled Discord command registration through the real VS 1.22.7 legacy shim/command dispatcher; normal chat player reaches link handler; null privilege reproduces incomplete-command error; admin tools retain controlserver; duplicate registration preserves owner. No Discord connection or live world.");

public class Proxy:DispatchProxy{public Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);}
public class Fixture {
 public object Commands=null!;public List<string> Replies=new();readonly Dictionary<Type,object> proxies=new();
 public object? Make(Type t){
  if(t==typeof(void))return null;if(t==typeof(string))return "";if(t.IsArray)return Array.CreateInstance(t.GetElementType()!,0);if(t.IsValueType)return Activator.CreateInstance(t);if(!t.IsInterface)return null;
  if(t.Name=="IServerPlayer"){TestPlayer.Fixture=this;return TestPlayer.Create();}
  if(proxies.TryGetValue(t,out var value))return value;value=DispatchProxy.Create(t,typeof(Proxy));((Proxy)value).Handler=(m,a)=>{
   if(m.Name=="RegisterCommand"){var method=Commands.GetType().GetMethods().Single(x=>x.Name=="RegisterCommand"&&x.GetParameters().Length==5);return method.Invoke(Commands,a);}
   if(m.Name=="get_Side")return Enum.Parse(m.ReturnType,"Server");
   if(m.Name=="get_PlayerUID")return "fixture-player";if(m.Name=="get_PlayerName")return "Alice";if(m.Name=="get_LanguageCode")return "en";
   if(m.Name=="SendMessage"){Replies.Add((string)a![1]!);return null;}
   return Make(m.ReturnType);
  };proxies[t]=value;return value;
 }
}
public static class CommandChecks {
 public static void Run(Fixture fixture,object service,object config){
  var api=(Vintagestory.API.Common.IChatCommandApi)fixture.Commands;
  if(api.Get("discordlink").Incomplete||((Vintagestory.Common.ChatCommandImpl)api.Get("discordlink")).GetPrivilege()!="chat"||((Vintagestory.Common.ChatCommandImpl)api.Get("discordlinkadmin")).GetPrivilege()!="controlserver")throw new Exception("Incorrect command completeness/access");
  var args=new Vintagestory.API.Common.TextCommandCallingArgs{Caller=new Vintagestory.API.Common.Caller{CallerPrivileges=new[]{"chat"}},RawArgs=new Vintagestory.API.Common.CmdArgs("")};
  typeof(Vintagestory.API.Common.Caller).GetField("player",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(args.Caller,fixture.Make(typeof(Vintagestory.API.Server.IServerPlayer)));
  api.Execute("discordlink",args,_=>{});
  if(fixture.Replies.Count!=1||!fixture.Replies[0].Contains("currently disabled"))throw new Exception("Normal player did not reach actual handler");
  config.GetType().GetProperty("Enabled")!.SetValue(config,true);
  args.RawArgs=new Vintagestory.API.Common.CmdArgs("status");api.Execute("discordlink",args,_=>{});
  if(!fixture.Replies.Last().Contains("not linked"))throw new Exception("Status command failed for normal player");
  args.RawArgs=new Vintagestory.API.Common.CmdArgs("");api.Execute("discordlink",args,_=>{});
  if((int)service.GetType().GetProperty("PendingCodeCount")!.GetValue(service)! !=1)throw new Exception("Bare /discordlink did not generate a code");
  args.RawArgs=new Vintagestory.API.Common.CmdArgs("cancel");api.Execute("discordlink",args,_=>{});
  if((int)service.GetType().GetProperty("PendingCodeCount")!.GetValue(service)! !=0)throw new Exception("Cancel left a pending code");
  var engine=(Vintagestory.Common.ChatCommandApi)api;
  #pragma warning disable CS0618
  if(engine.RegisterCommand("discordlink","duplicate","",(_,_,_)=>{},"chat"))throw new Exception("Duplicate ownership replaced");
  engine.RegisterCommand("legacy-null","regression","",(_,_,_)=>{},null);
  #pragma warning restore CS0618
  try{api.Execute("legacy-null",args,_=>{});throw new Exception("Original null privilege no longer reproduced");}
  catch(InvalidOperationException e) when(e.Message.Contains("Incomplete command")){}
 }
}
