// Minimal API doubles used to compile and exercise the production command handler.
// They do not prove Vintage Story binary compatibility or real world persistence.
namespace Newtonsoft.Json {
 [AttributeUsage(AttributeTargets.Property)]public sealed class JsonIgnoreAttribute:Attribute {}
 public enum Formatting {None,Indented}
 public static class JsonConvert {
 public static string SerializeObject(object o,Formatting f=Formatting.None)=>System.Text.Json.JsonSerializer.Serialize(o,new System.Text.Json.JsonSerializerOptions{WriteIndented=f==Formatting.Indented});
 public static T? DeserializeObject<T>(string s)=>System.Text.Json.JsonSerializer.Deserialize<T>(s);
 }
}
namespace Vintagestory.API.MathTools {
 public class BlockPos {public int X,Y,Z,dimension;public BlockPos(int x,int y,int z,int d){X=x;Y=y;Z=z;dimension=d;}public string Key=>$"{dimension}:{X}:{Y}:{Z}";}
}
namespace Vintagestory.API.Datastructures {public class TreeAttribute {public void ToBytes(BinaryWriter w)=>w.Write(0);} }
namespace Vintagestory.API.Common.Entities {
 public class Entity {public long EntityId;public Vintagestory.API.MathTools.BlockPos Position=new(0,100,0,0);}
 public class EntityAgent:Entity {} public class EntityPlayer:EntityAgent {}
}
namespace Vintagestory.API.Common {
 using Vintagestory.API.MathTools;using Vintagestory.API.Datastructures;
 public enum EnumBlockAccessFlags {BuildOrBreak}
 public enum EnumWorldAccessResponse {Granted,LandClaimed}
 public enum EnumChatType {CommandSuccess,CommandError} public static class BlockLayersAccess {public const int Solid=0,Fluid=1;}
 public class CmdArgs {readonly Queue<string> q;public CmdArgs(string text)=>q=new(text.Split(' ',StringSplitOptions.RemoveEmptyEntries));public string? PopWord()=>q.Count>0?q.Dequeue():null;public int Length=>q.Count;}
 public class AssetLocation {readonly string code;public AssetLocation(string c)=>code=c.Contains(':')?c:"game:"+c;public string Path=>code.Split(':').Last();public string ToShortString()=>code;}
 public class BlockSelection {public BlockPos? Position;}
 public class CollectibleObject {public AssetLocation Code=new("air");public int MaxStackSize=64;}
 public class Item:CollectibleObject {}
 public class Block:CollectibleObject {public int BlockId;public int Replaceable=6000;public string? EntityClass;public bool ForFluidsLayer;}
 public class ItemStack {public CollectibleObject Collectible;public int StackSize;public ItemStack(CollectibleObject c,int n){Collectible=c;StackSize=n;}public ItemStack Clone()=>new(Collectible,StackSize);}
 public class ItemSlot {public ItemStack? Itemstack;public void MarkDirty(){} }
 public interface IInventory {int Count {get;} ItemSlot this[int i] {get;}}
 public class InventoryBase:IInventory {public event Action<int>? SlotModified;public int Count=>0;public ItemSlot this[int i]=>new();}
 public interface IBlockEntityContainer {IInventory Inventory {get;}}
 public class BlockEntity {public void ToTreeAttributes(TreeAttribute t){}public void MarkDirty(bool b){} }
}
namespace Vintagestory.API.Server {
 using Vintagestory.API.Common;using Vintagestory.API.Common.Entities;using Vintagestory.API.MathTools;
 public interface IServerPlayer {string PlayerUID {get;}string PlayerName {get;} Entity Entity {get;} void SendMessage(int g,string t,EnumChatType type);}
 public delegate void CommandHandler(IServerPlayer p,int g,CmdArgs args);
 public interface ICoreServerAPI {FakeWorld World {get;}FakeEvents Event {get;}FakePermissions Permissions {get;}FakeLogger Logger {get;}void RegisterCommand(string n,string d,string h,CommandHandler c,string priv);}
 public class FakePermissions {public void RegisterPrivilege(string n,string h,bool a){} }
 public class FakeLogger {public void Error(string m,params object[] a){} }
 public class FakeEvents {
 public event Action<IServerPlayer,int,BlockSelection>? DidBreakBlock;
 public event Action<IServerPlayer,int,BlockSelection,ItemStack>? DidPlaceBlock;
 public event Action<IServerPlayer,BlockSelection>? DidUseBlock;
 public readonly System.Collections.Concurrent.ConcurrentQueue<Action> Main=new();public readonly Queue<Action<float>> Callbacks=new();
 public long RegisterGameTickListener(Action<float> a,int ms)=>1;
 public void UnregisterGameTickListener(long id){}
 public void EnqueueMainThreadTask(Action a,string n)=>Main.Enqueue(a);
 public void RegisterCallback(Action<float> a,int ms)=>Callbacks.Enqueue(a);
 public void Drain(){while(Main.TryDequeue(out var action))action();}
 }
 public class FakeAccessor {
 readonly FakeWorld world;public int Writes;public Action? BeforeWrite;public readonly Dictionary<string,int> Blocks=new();public FakeAccessor(FakeWorld w)=>world=w;
 public bool IsValidPos(BlockPos p)=>true;public object GetChunkAtBlockPos(BlockPos p)=>new();
 public Block GetBlock(BlockPos p,int layer=0)=>world.Blocks.Values.First(b=>b.BlockId==(layer==1?0:Blocks.GetValueOrDefault(p.Key)));
 public BlockEntity? GetBlockEntity(BlockPos p)=>null;public Block[]? GetDecors(BlockPos p)=>null;
 public void SetBlock(int id,BlockPos p,int layer){BeforeWrite?.Invoke();Writes++;Blocks[p.Key]=id;}public void MarkBlockDirty(BlockPos p){}
 }
 public class FakeRegistry {public Entity CreateEntity(object type)=>new EntityAgent();}
 public class FakeClaims {
 public bool Claimed,Deny;
 public IEnumerable<object> Get(BlockPos p)=>Claimed?new object[]{new object()}:Array.Empty<object>();
 public EnumWorldAccessResponse TestAccess(IServerPlayer p,BlockPos pos,EnumBlockAccessFlags f)=>Deny?EnumWorldAccessResponse.LandClaimed:EnumWorldAccessResponse.Granted;
 }
 public class FakeWorld {
 public IServerPlayer[] AllOnlinePlayers=Array.Empty<IServerPlayer>();public string SavegameIdentifier="world";
 public FakeAccessor BlockAccessor;public FakeRegistry ClassRegistry=new();public FakeClaims Claims=new();
 public Dictionary<string,Block> Blocks=new(){{"game:air",new(){BlockId=0,Code=new("air")}},{"game:stone",new(){BlockId=1,Code=new("stone")}}};
 public FakeWorld()=>BlockAccessor=new(this);
 public Block? GetBlock(AssetLocation l)=>Blocks.GetValueOrDefault(l.ToShortString());public Item? GetItem(AssetLocation l)=>null;
 public Entity? GetEntityById(long id)=>null;public object GetEntityType(AssetLocation l)=>new();public void SpawnEntity(Entity e){}
 }
}
namespace BlockLogger.Services {
 using Vintagestory.API.Common.Entities;using Vintagestory.API.MathTools;using BlockLogger.Models;
 public static class EntityPositionResolver {
 public static bool TryGetBlockPos(Entity? e,out BlockPos p){p=e?.Position??new(0,0,0,0);return e!=null;}
 public static bool TrySetPose(Entity e,double x,double y,double z,float yaw,float pitch)=>true;
 }
 public sealed class LogStorageService {
 public string DatabasePath="";public RollbackHistory History=new(){WorldId="world"};
 public readonly List<(string Target,DateTime From,DateTime To,RecoveryCell? Center,int Radius)> Queries=new();
 public Task<RollbackHistory> QueryRollbackRecoveryAsync(string target,DateTime from,DateTime to,RecoveryCell? center=null,int radius=0,CancellationToken cancellationToken=default){cancellationToken.ThrowIfCancellationRequested();Queries.Add((target,from,to,center,radius));History.AreaMode=center!=null;History.PlayerUid=target;return Task.FromResult(History);}
 public bool TryEnqueue(LogEventRecord r)=>true;
 }
}
