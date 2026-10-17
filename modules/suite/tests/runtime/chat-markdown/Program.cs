using System.Reflection;
using System.Runtime.Loader;
using System.Collections;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new Exception("Set VINTAGE_STORY");
string suiteDll = Environment.GetEnvironmentVariable("NSS_ASSEMBLY") ?? throw new Exception("Set NSS_ASSEMBLY");
string guardDll = Environment.GetEnvironmentVariable("CHATGUARD_ASSEMBLY") ?? throw new Exception("Set CHATGUARD_ASSEMBLY");
AssemblyLoadContext.Default.Resolving += (context,name) => {
    foreach (string root in new[] {game,Path.Combine(game,"Lib"),Path.Combine(game,"Mods"),Path.GetDirectoryName(suiteDll)!,Path.GetDirectoryName(guardDll)!}) {
        string path=Path.Combine(root,name.Name+".dll");if(File.Exists(path))return context.LoadFromAssemblyPath(path);
    }
    return null;
};
var suite=AssemblyLoadContext.Default.LoadFromAssemblyPath(suiteDll);
var guard=AssemblyLoadContext.Default.LoadFromAssemblyPath(guardDll);
string temporary=Path.Combine(Path.GetTempPath(),"nss-markdown-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
var resources = new List<IDisposable>();
try {
    var fixture=new Fixture(temporary);
    var chatType=suite.GetType("BlockLogger.Services.ChatDisplayService",true)!;
    var ctor=chatType.GetConstructors().Single();
    object chat=ctor.Invoke(new[]{fixture.Make(ctor.GetParameters()[0].ParameterType),null,null});
    resources.Add((IDisposable)chat);
    chatType.GetMethod("Start")!.Invoke(chat,null);
    object chatConfig=chatType.GetProperty("Config")!.GetValue(chat)!;
    if(fixture.CommandHandler==null||fixture.CommandName!="chatformat"||!fixture.CommandRequiresPlayer||fixture.CommandPrivilege!="chat")throw new Exception("/chatformat was not registered with player/chat access");
    var playerInterface=AssemblyLoadContext.Default.Assemblies.Single(a=>a.GetName().Name=="VintagestoryAPI")
        .GetType("Vintagestory.API.Server.IServerPlayer",true)!;
    object commandArgs=GuideSmoke.Args(fixture.Make(playerInterface)!);
    string Guide()=>GuideSmoke.Message(fixture.CommandHandler.DynamicInvoke(commandArgs)!);
    if(!Guide().Contains("Colors (off for you)")||!Guide().Contains("**text**"))throw new Exception("Default guide incorrect");
    chatConfig.GetType().GetProperty("MarkdownAllowColors")!.SetValue(chatConfig,true);
    chatConfig.GetType().GetProperty("MarkdownAllowLinks")!.SetValue(chatConfig,true);
    if(!Guide().Contains("Colors (enabled)")||!Guide().Contains("Links (enabled)"))throw new Exception("Guide did not read live settings");
    var guardType=guard.GetType("NewfiesChatGuard.ChatGuardService",true)!;
    object moderation=Activator.CreateInstance(guardType,fixture.Make(guardType.GetConstructors().Single().GetParameters()[0].ParameterType))!;
    resources.Add((IDisposable)moderation);
    object config=guardType.GetProperty("Config")!.GetValue(moderation)!;
    config.GetType().GetProperty("NotifyPlayers")!.SetValue(config,false);
    foreach(string name in new[]{"Spam","Repeat","Caps","Links","Mentions","Length"}) {
        object rule=config.GetType().GetProperty(name)!.GetValue(config)!;
        rule.GetType().GetProperty("Enabled")!.SetValue(rule,false);
    }
    var evaluate=guardType.GetMethod("EvaluateCached",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var player=fixture.Make(evaluate.GetParameters()[0].ParameterType)!;
    long Scanned()=>(long)guardType.GetField("scannedMessages",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(moderation)!;
    object Evaluate(string body)=>evaluate.Invoke(moderation,new[]{player,0,body,"test"})!;
    var formatter=suite.GetType("BlockLogger.Services.ChatMarkdown",true)!.GetMethod("Format")!;
    long beforeGuide=Scanned();Guide();if(Scanned()!=beforeGuide)throw new Exception("Guide entered moderation path");
    foreach(string raw in new[]{"[color=#80C8FF]hello[/color]", "[Community](https://newfiescommunity.com)", "**nested *styles* here**", "\\*escaped\\*", "[color=#FFFFFF]outer [color=#80C8FF]inner[/color] outer[/color]", "**bold *italic***", "*italic **bold***", "[Wiki](https://example.com/Thing_(game))"}) {
        long before=Scanned();
        object result=formatter.Invoke(null,new object[]{raw,true,true})!;
        string markup=(string)result.GetType().GetProperty("Vtml")!.GetValue(result)!;
        string plain=(string)result.GetType().GetProperty("ModerationText")!.GetValue(result)!;
        Evaluate(raw);Evaluate(plain);Evaluate(markup);
        if(Scanned()!=before+1)throw new Exception("Message counted more than once across raw/visible/VTML: "+raw);
        // Exercise the game's actual VTML parser with every generated tag.
        var api=AssemblyLoadContext.Default.Assemblies.Single(a=>a.GetName().Name=="VintagestoryAPI");
        var parser=api.GetType("Vintagestory.API.Common.VtmlParser",true)!;
        var tokenMethod=parser.GetMethods(BindingFlags.Public|BindingFlags.Static).Single(m=>m.Name=="Tokenize");
        var argsForParser=tokenMethod.GetParameters().Select(p=>p.ParameterType==typeof(string)?(object)markup:null).ToArray();
        var tokens=(Array)tokenMethod.Invoke(null,argsForParser)!;
        if(tokens.Length==0)throw new Exception("Game parser produced no tokens");
    }
    fixture.Listeners["newfieschatguard:control"] = guardType.GetMethod("OnControlEvent",BindingFlags.Instance|BindingFlags.NonPublic)!
        .CreateDelegate(fixture.Listeners.Values.First().GetType(),moderation);
    chatConfig.GetType().GetProperty("ShowTimestampBeforeMessage")!.SetValue(chatConfig,true);
    var onChat=chatType.GetMethod("OnPlayerChat",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var consumed=Activator.CreateInstance(onChat.GetParameters()[4].ParameterType)!;
    foreach(bool guardFirst in new[]{true,false}) {
        string raw=guardFirst?"[color=#C080FF]timestamp one[/color]":"**timestamp two**";
        long before=Scanned();
        if(guardFirst)Evaluate(raw);
        var chatArgs=new object[]{player,0,raw,"routing-payload",consumed};
        onChat.Invoke(chat,chatArgs);
        string emitted=(string)chatArgs[2];
        if(!emitted.Contains("timestamp")||(string)chatArgs[3]!="routing-payload")throw new Exception("Live chat handler lost text/routing");
        string body=emitted[(emitted.IndexOf(": ",StringComparison.Ordinal)+2)..];
        Evaluate(body);
        if(Scanned()!=before+1)throw new Exception("Timestamp/handler order counted a message twice: "+guardFirst);
    }
    object blocked=config.GetType().GetProperty("BlockedTerms")!.GetValue(config)!;
    blocked.GetType().GetProperty("Enabled")!.SetValue(blocked,true);
    blocked.GetType().GetProperty("Action")!.SetValue(blocked,"Block");
    var terms=blocked.GetType().GetProperties().First(p=>p.PropertyType==typeof(List<string>));terms.SetValue(blocked,new List<string>{"forbiddenword"});
    object decision=Evaluate("for**bidden**word");
    if(!(bool)decision.GetType().GetProperty("Blocked")!.GetValue(decision)! || (string)decision.GetType().GetProperty("Rule")!.GetValue(decision)! != "BlockedTerm")throw new Exception("Formatting hid a blocked term");
    var consoleType=suite.GetType("BlockLogger.WebConsole.WebConsoleService",true)!;
    object console=consoleType.GetConstructors().Single().Invoke(new[]{fixture.Make(consoleType.GetConstructors().Single().GetParameters()[0].ParameterType),null});
    resources.Add((IDisposable)console);
    var capture=consoleType.GetMethod("OnPlayerChat",BindingFlags.Instance|BindingFlags.NonPublic)!;
    var captured=(ICollection)consoleType.GetField("publicChat",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(console)!;
    foreach(bool captureFirst in new[]{true,false}) {
        string raw=captureFirst?"**relay before**":"[color=#80C8FF]relay after[/color]";
        long before=Scanned(); int count=captured.Count;
        var chatArgs=new object[]{player,0,raw,"routing-payload",Activator.CreateInstance(consumed.GetType())!};
        if(captureFirst)capture.Invoke(console,chatArgs);
        onChat.Invoke(chat,chatArgs);
        if(!captureFirst)capture.Invoke(console,chatArgs);
        string last=((IEnumerable)captured).Cast<string>().Last();
        if(captured.Count!=count+1 || last.Contains("<font") || last.Contains("<strong") || last.Contains("[color") || last.Contains("**"))throw new Exception("Console presentation order leaked formatting: order="+captureFirst+" count="+count+"/"+captured.Count+" line="+last);
        if(Scanned()!=before+1)throw new Exception("Relay presentation counted moderation twice");
    }
    foreach(bool captureFirst in new[]{true,false}) {
        int count=captured.Count;
        var chatArgs=new object[]{player,0,"for**bidden**word","routing-payload",Activator.CreateInstance(consumed.GetType())!};
        if(captureFirst)capture.Invoke(console,chatArgs);
        onChat.Invoke(chat,chatArgs);
        if(!captureFirst)capture.Invoke(console,chatArgs);
        if(captured.Count!=count)throw new Exception("Console relayed blocked chat in handler order "+captureFirst);
    }
    int serial = 0;
    var guardChat=guardType.GetMethod("OnPlayerChat",BindingFlags.Instance|BindingFlags.NonPublic)!;
    foreach (bool markdownOn in new[] { true, false })
    foreach (string layout in new[] { "{timestamp} {role} {name}: {message}", "[Global] {role} {name} {timestamp}: {message} [end]", "{name} → {message}" })
    foreach (bool guardFirst in new[] { true, false })
    {
        chatConfig.GetType().GetProperty("MarkdownEnabled")!.SetValue(chatConfig,markdownOn);
        chatConfig.GetType().GetProperty("ChatFormat")!.SetValue(chatConfig,layout);
        string body="layout sample "+(++serial),raw=markdownOn?"**"+body+"**":body;
        long before=Scanned();int count=captured.Count;
        var chatArgs=new object[]{player,0,"Alice: "+raw,"routing-payload",Activator.CreateInstance(consumed.GetType())!};
        if(guardFirst)guardChat.Invoke(moderation,chatArgs);
        onChat.Invoke(chat,chatArgs);
        if(!guardFirst)guardChat.Invoke(moderation,chatArgs);
        capture.Invoke(console,chatArgs);
        if(Scanned()!=before+1)throw new Exception("Custom layout double moderation: "+layout+" markdown="+markdownOn+" order="+guardFirst);
        string last=((IEnumerable)captured).Cast<string>().Last();
        if(captured.Count!=count+1||!last.EndsWith(body)||last.Contains("[Global]")||last.Contains("[end]"))throw new Exception("Custom layout leaked into Console: "+last);
        if(PresentationSmoke.Query(fixture,(string)chatArgs[2])!=body)throw new Exception("Raw-line presentation bridge lost message");
    }
    chatConfig.GetType().GetProperty("MarkdownEnabled")!.SetValue(chatConfig,true);
    Console.WriteLine("PASS UI2: 12 actual Suite/ChatGuard/Console handler combinations across custom name/time/prefix/suffix layouts, Markdown on/off and guard ordering; single moderation sample and canonical relay content.");
    // Discord queries the same presentation event; test its returned plain text and blocked flag.
    PresentationSmoke.Check(fixture);
    string html=(string)suite.GetType("BlockLogger.WebDashboard.WebDashboardPage",true)!.GetField("Html",BindingFlags.Public|BindingFlags.Static)!.GetValue(null)!;
    if(args.Length>0)File.WriteAllText(args[0],html);
    foreach(string id in new[]{"chatMarkdownEnabled","chatMarkdownColors","chatMarkdownLinks","chatMarkdownRoles"})if(!html.Contains("id=\""+id+"\""))throw new Exception("Missing webpanel control "+id);
    Console.WriteLine("PASS: compiled /chatformat registration and private guide settings; Suite + ChatGuard canonical moderation, single spam sample across raw/visible/VTML, blocked term split across styles, game VTML parser and dashboard controls; console relay before/after formatting, shared Discord plain-text bridge, blocked-message relay suppression. No game world started.");
} finally {foreach(var service in resources.AsEnumerable().Reverse())service.Dispose();Directory.Delete(temporary,true);}

public class ApiProxy : DispatchProxy {
    public Fixture Fixture=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args) {
        switch(method!.Name){
            case "Get" when method.DeclaringType?.Name=="IChatCommandApi":return null;
            case "Create" when method.DeclaringType?.Name=="IChatCommandApi":Fixture.CommandName=(string)args![0]!;return Fixture.Make(method.ReturnType);
            case "RequiresPlayer":Fixture.CommandRequiresPlayer=true;return Fixture.Make(method.ReturnType);
            case "RequiresPrivilege":Fixture.CommandPrivilege=(string)args![0]!;return Fixture.Make(method.ReturnType);
            case "HandleWith":Fixture.CommandHandler=(Delegate)args![0]!;return Fixture.Make(method.ReturnType);
            case "GetOrCreateDataPath":return Fixture.Root;
            case "StoreModConfig":return null;
            case "LoadModConfig":return null;
            case "get_PlayerUID":return "test-player";
            case "get_PlayerName":return "Alice";
            case "get_Code":case "get_Name":return "player";
            case "get_AllOnlinePlayers":var a=Array.CreateInstance(method.ReturnType.GetElementType()!,1);a.SetValue(Fixture.Player,0);return a;
            case "RegisterEventBusListener":Fixture.Listeners[args!.OfType<string>().Last()]=args!.OfType<Delegate>().First();return null;
            case "PushEvent":if(Fixture.Listeners.TryGetValue((string)args![0]!,out var listener)) {
                var parameters=listener.Method.GetParameters();
                listener.DynamicInvoke(args[0],Activator.CreateInstance(parameters[1].ParameterType.GetElementType()!),args[1]);
            }return null;
        }
        return Fixture.Make(method.ReturnType);
    }
}
public class Fixture(string root) {
    public string Root=root; public object? Player;
    public string CommandName="",CommandPrivilege="";public bool CommandRequiresPlayer;public Delegate? CommandHandler;
    public Dictionary<string,Delegate> Listeners=new();
    readonly Dictionary<Type,object> proxies=new();
    public object? Make(Type type) {
        if(type==typeof(void))return null;if(type==typeof(string))return "";
        if(type.IsArray)return Array.CreateInstance(type.GetElementType()!,0);
        if(type.IsValueType)return Activator.CreateInstance(type);
        if(type.Name=="IServerPlayer")return Player ??= TestPlayer.Create();
        if(!type.IsInterface)return type.GetConstructor(Type.EmptyTypes)!=null?Activator.CreateInstance(type):null;
        if(proxies.TryGetValue(type,out var cached))return cached;
        var proxy=DispatchProxy.Create(type,typeof(ApiProxy));((ApiProxy)proxy).Fixture=this;proxies[type]=proxy;
        if(type.Name=="IServerPlayer")Player=proxy;
        return proxy;
    }
}

// Keep references to the game's types out of Main's initial JIT, before the resolver is installed.
public static class GuideSmoke {
    public static object Args(object player) {
        var args=new Vintagestory.API.Common.TextCommandCallingArgs {Caller=new Vintagestory.API.Common.Caller()};
        typeof(Vintagestory.API.Common.Caller).GetField("player",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(args.Caller,player);
        return args;
    }
    public static string Message(object result)=>((Vintagestory.API.Common.TextCommandResult)result).StatusMessage;
}

public static class PresentationSmoke {
    public static string Query(Fixture fixture,string rawLine) {
        var query=new Vintagestory.API.Datastructures.TreeAttribute();query.SetString("playerUid","test-player");query.SetInt("channelId",0);query.SetString("body","stripped presentation");query.SetString("rawLine",rawLine);
        ((Vintagestory.API.Server.ICoreServerAPI)fixture.Make(typeof(Vintagestory.API.Server.ICoreServerAPI))!).Event.PushEvent("newfiesse:chat-presentation-query",query);
        return query.GetString("visibleText");
    }

    public static void Check(Fixture fixture) {
    var presentation = new Vintagestory.API.Datastructures.TreeAttribute();
    presentation.SetString("playerUid","test-player"); presentation.SetInt("channelId",0); presentation.SetString("body","**discord plain**");
    ((Vintagestory.API.Server.ICoreServerAPI)fixture.Make(typeof(Vintagestory.API.Server.ICoreServerAPI))!).Event.PushEvent("newfiesse:chat-presentation-query",presentation);
    if(!presentation.GetBool("available") || presentation.GetString("visibleText")!="discord plain")throw new Exception("Discord presentation bridge did not return plain text");
    presentation.SetString("body","for**bidden**word");
    ((Vintagestory.API.Server.ICoreServerAPI)fixture.Make(typeof(Vintagestory.API.Server.ICoreServerAPI))!).Event.PushEvent("newfiesse:chat-presentation-query",presentation);
    if(!presentation.GetBool("blocked"))throw new Exception("Discord presentation bridge did not block moderated chat");
    }
}
