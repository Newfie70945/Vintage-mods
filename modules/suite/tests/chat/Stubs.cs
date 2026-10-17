// Deliberately narrow test doubles; these do not validate the game's binary API.
namespace Vintagestory.API.Common {
 public enum EnumHandling { PassThrough }
 public interface IPlayerRole { string Name {get;} string Code {get;} System.Drawing.Color Color {get;} }
 public interface IPlayer { string PlayerUID {get;} string PlayerName {get;} IPlayerRole? Role {get;} }
 public static class Privilege { public const string chat="chat"; }
 public class Caller { public IPlayer? Player; }
 public class TextCommandCallingArgs { public Caller Caller=new(); }
 public class TextCommandResult { public string StatusMessage=""; public bool Failed; public static TextCommandResult Success(string text)=>new(){StatusMessage=text}; public static TextCommandResult Error(string text)=>new(){StatusMessage=text,Failed=true}; }
 public class FakeCommand {
 public Func<TextCommandCallingArgs,TextCommandResult>? Handler; public bool PlayerRequired; public string Privilege="";
 public FakeCommand WithDescription(string value)=>this; public FakeCommand RequiresPlayer(){PlayerRequired=true;return this;}
 public FakeCommand RequiresPrivilege(string value){Privilege=value;return this;} public FakeCommand HandleWith(Func<TextCommandCallingArgs,TextCommandResult> handler){Handler=handler;return this;}
 }
 public class FakeCommands { public readonly Dictionary<string,FakeCommand> Commands=new(); public FakeCommand? Get(string code)=>Commands.GetValueOrDefault(code); public FakeCommand Create(string code)=>Commands[code]=new(); }
}
namespace Vintagestory.API.Config { public static class GlobalConstants {public const int GeneralChatGroup=0;} }
namespace Vintagestory.API.Datastructures {
 public interface IAttribute {}
 public interface ITreeAttribute:IAttribute { string GetString(string key,string fallback=""); bool GetBool(string key,bool fallback=false); void SetString(string key,string value); void SetBool(string key,bool value); int GetInt(string key,int fallback=0); void SetInt(string key,int value); }
 public class TreeAttribute:ITreeAttribute {
 readonly Dictionary<string,object> values=new();
 public string GetString(string k,string f="")=>values.TryGetValue(k,out var v)?(string)v:f;
 public bool GetBool(string k,bool f=false)=>values.TryGetValue(k,out var v)?(bool)v:f;
 public int GetInt(string k,int f=0)=>values.TryGetValue(k,out var v)?(int)v:f; public void SetInt(string k,int v)=>values[k]=v;
 public void SetString(string k,string v)=>values[k]=v; public void SetBool(string k,bool v)=>values[k]=v;
 }
 public class BoolRef {public bool value;}
}
namespace Vintagestory.API.Server {
 using Vintagestory.API.Common; using Vintagestory.API.Datastructures;
 public interface IServerPlayer:IPlayer {}
 public delegate void ChatHandler(IServerPlayer p,int channel,ref string message,ref string data,BoolRef consumed);
 public delegate void BusHandler(string name,ref EnumHandling handling,IAttribute data);
 public interface ICoreServerAPI { FakeEvents Event {get;} FakeWorld World {get;} FakeLogger Logger {get;} FakeCommands ChatCommands {get;} }
 public class FakeEvents {
 public event ChatHandler? PlayerChat;
 readonly Dictionary<string,BusHandler> bus=new();
 public void RegisterEventBusListener(BusHandler h,string filterByEventName)=>bus[filterByEventName]=h;
 public void PushEvent(string n,IAttribute t) {var h=EnumHandling.PassThrough;if(bus.TryGetValue(n,out var b))b(n,ref h,t);}
 public void Chat(IServerPlayer p,int c,ref string m,ref string d,BoolRef b)=>PlayerChat?.Invoke(p,c,ref m,ref d,b);
 }
 public class FakeWorld {public IPlayer[] AllOnlinePlayers {get;set;}=Array.Empty<IPlayer>();}
 public class FakeLogger { public int Warnings; public void Notification(string m,params object[] a){} public void Warning(string m,params object[] a)=>Warnings++; }
}
namespace BlockLogger.Services {
 public static class ModConfigRecovery {public static T? LoadOrNull<T>(Vintagestory.API.Server.ICoreServerAPI a,string f,string n) where T:class=>null;}
 public static class SuiteConfigStorage {public static void Store<T>(Vintagestory.API.Server.ICoreServerAPI a,T c,string f){} }
}
namespace BlockLogger.Integrations {
 public class Decision {public bool Available=true; public bool Blocked;}
 public static class ChatGuardControlBridge {
 public static int Calls;public static bool Block;public static string Body="";
 public static Decision Moderate(Vintagestory.API.Server.ICoreServerAPI a,Vintagestory.API.Server.IServerPlayer p,int c,string m){Calls++;Body=m;return new(){Blocked=Block};}
 }
}
