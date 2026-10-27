using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace NewfiesOptimizer;

// Optional same-cell consolidation. No terrain queries, radius pulls, global
// entity snapshots, inventory caches or oversized stacks. Native slot merging
// owns the quantity/attribute transfer; only an emptied source is removed.
public sealed class DroppedItemClumping : IDisposable
{
    public const int MaxScan = 128, MaxMerges = 16, MaxCells = 2048;
    private readonly ICoreServerAPI api;
    private readonly Func<string?> runtimeGuard;
    private IEnumerator<KeyValuePair<long,Entity>>? cursor;
    private readonly Dictionary<Cell,long> cells = new();
    private long listener = -1, nextConflictCheck;
    private bool disposed;
    private string state = "disabled", reason = "Independent module switch is off.";
    private long scanned, merges, moved, removed, failures;
    private readonly record struct Cell(int Dimension,int X,int Y,int Z,int ItemId,EnumItemClass Kind,string? Owner);
    public ModuleStatus Status => new("droppedItemClumping",state,reason);
    public IReadOnlyDictionary<string,long> Counters => new Dictionary<string,long> {
        ["droppedItemsScanned"]=scanned,["droppedItemMerges"]=merges,["droppedItemsMoved"]=moved,
        ["droppedItemEntitiesRemoved"]=removed,["droppedItemClumpingFailures"]=failures,["droppedItemCells"]=cells.Count };
    internal static MethodBase[] Dependencies => new MethodBase?[] {
        AccessTools.Method(typeof(ItemSlot),nameof(ItemSlot.TryPutInto),[typeof(IWorldAccessor),typeof(ItemSlot),typeof(int)]),
        AccessTools.Method(typeof(ItemSlot),nameof(ItemSlot.TryPutInto),[typeof(ItemSlot),typeof(ItemStackMoveOperation).MakeByRefType()]),
        AccessTools.Method(typeof(CollectibleObject),nameof(CollectibleObject.TryMergeStacks)),
        AccessTools.Method(typeof(CollectibleObject),nameof(CollectibleObject.GetMergableQuantity)),
        AccessTools.Method(typeof(Entity),nameof(Entity.Die)),
        AccessTools.Method(typeof(ItemStack),nameof(ItemStack.Equals),[typeof(IWorldAccessor),typeof(ItemStack),typeof(string[])])
    }.Select(m=>m??throw new MissingMethodException("Dropped-item native merge contract unavailable.")).ToArray();
    public DroppedItemClumping(ICoreServerAPI api, OptimizerConfig config, Func<string?> runtimeGuard)
    {
        this.api=api;this.runtimeGuard=runtimeGuard;
        if(!config.EngineOptimizations.DroppedItemClumping)return;
        if(!config.Enabled||config.Compatibility.SafeMode){state="blocked";reason="Master switch or Safe Mode retains stock dropped items.";return;}
        try
        {
            if(runtimeGuard() is string blocked){state="blocked";reason=blocked;return;}
            if(Conflict() is string conflict){state="conflict";reason=conflict;return;}
            listener=api.Event.RegisterGameTickListener(_=>Tick(),100);
            state="active";reason="Bounded same-cell item merging; exact attributes, owner and native stack limits retained.";
        }
        catch(Exception e){Stop("failed","Item clumping initialization failed: "+e.GetType().Name);}
    }
    private static string? Conflict()
    {
        foreach(var method in Dependencies)
            if(Harmony.GetPatchInfo(method) is {} info && info.Owners.FirstOrDefault(owner=>owner!=EngineOptimizations.HarmonyId&&owner!=EngineOptimizations.ProfilerHarmonyId) is string owner)
                return "Foreign Harmony owner on "+method.Name+": "+owner;
        return null;
    }
    internal static bool Eligible(EntityItem item,long now)
    {
        var stack=item.Itemstack;
        if(item.GetType()!=typeof(EntityItem)||!item.Alive||item.Slot?.GetType()!=typeof(EntityItemSlot)||stack?.Collectible is not {} collectible||
           stack.StackSize<=0||stack.StackSize>=collectible.MaxStackSize||collectible.MaxStackSize<=1||!ReferenceEquals(stack,item.Slot.Itemstack)||
           now<item.itemSpawnedMilliseconds||now-item.itemSpawnedMilliseconds<2000||!item.OnGround||item.Swimming||item.InLava||item.IsOnFire||
           !double.IsFinite(item.Pos.X)||!double.IsFinite(item.Pos.Y)||!double.IsFinite(item.Pos.Z)||
           item.Pos.X<int.MinValue||item.Pos.X>=int.MaxValue||item.Pos.Y<int.MinValue||item.Pos.Y>=int.MaxValue||item.Pos.Z<int.MinValue||item.Pos.Z>=int.MaxValue||
           !double.IsFinite(item.Pos.Motion.X)||!double.IsFinite(item.Pos.Motion.Y)||!double.IsFinite(item.Pos.Motion.Z)||
           item.Pos.Motion.LengthSq()>0.0001||item.SidedProperties?.Behaviors is not {} behaviors)return false;
        var assembly=collectible.GetType().Assembly;
        if(assembly!=typeof(CollectibleObject).Assembly&&assembly!=typeof(Vintagestory.GameContent.AiTaskManager).Assembly&&assembly.GetName().Name!="VSSurvivalMod")return false;
        if(collectible.CollectibleBehaviors?.Length>0||
           collectible.GetType().GetMethod(nameof(CollectibleObject.TryMergeStacks))?.DeclaringType!=typeof(CollectibleObject)||
           collectible.GetType().GetMethod(nameof(CollectibleObject.GetMergableQuantity))?.DeclaringType!=typeof(CollectibleObject))return false;
        foreach(var behavior in behaviors)
        {
            var owner=behavior.GetType().Assembly;
            if(owner!=typeof(EntityBehavior).Assembly&&owner!=typeof(Vintagestory.GameContent.AiTaskManager).Assembly&&owner!=typeof(Vintagestory.Server.ServerMain).Assembly&&owner.GetName().Name!="VSSurvivalMod")return false;
            if(behavior.PropertyName() is not ("passivephysics" or "despawn" or "floatupwhenstuck"))return false;
        }
        return true;
    }
    private static Cell Key(EntityItem item)=>new(item.Pos.Dimension,(int)Math.Floor(item.Pos.X),(int)Math.Floor(item.Pos.Y),(int)Math.Floor(item.Pos.Z),item.Itemstack.Id,item.Itemstack.Class,item.ByPlayerUid);
    internal static int Merge(IWorldAccessor world,EntityItem a,EntityItem b,long now)
    {
        if(ReferenceEquals(a,b)||a.EntityId==b.EntityId||!ReferenceEquals(a.World,world)||!ReferenceEquals(b.World,world)||!Eligible(a,now)||!Eligible(b,now)||Key(a)!=Key(b)||
           !string.Equals(a.ByPlayerUid,b.ByPlayerUid,StringComparison.Ordinal)||!ReferenceEquals(a.Itemstack.Collectible,b.Itemstack.Collectible)||
           !a.Itemstack.Attributes.Equals(world,b.Itemstack.Attributes,Array.Empty<string>())||!a.Itemstack.TempAttributes.Equals(world,b.Itemstack.TempAttributes,Array.Empty<string>()))return 0;
        // Keep the older entity, preserving its age and location. Never reset a
        // despawn timer or move the surviving drop towards another player.
        var sink=a.itemSpawnedMilliseconds<b.itemSpawnedMilliseconds||(a.itemSpawnedMilliseconds==b.itemSpawnedMilliseconds&&a.EntityId<b.EntityId)?a:b;
        var source=ReferenceEquals(sink,a)?b:a;
        long before=(long)sink.Slot.StackSize+source.Slot.StackSize;
        int transferred;
        try { transferred=source.Slot.TryPutInto(world,sink.Slot,source.Slot.StackSize); }
        finally
        {
            sink.Itemstack=sink.Slot.Itemstack;source.Itemstack=source.Slot.Itemstack;
            sink.WatchedAttributes.MarkPathDirty("itemstack");source.WatchedAttributes.MarkPathDirty("itemstack");
            if(source.Slot.Empty)source.Die(EnumDespawnReason.Removed);
        }
        if(sink.Itemstack?.Collectible is not {} resultCollectible||before!=(long)sink.Slot.StackSize+source.Slot.StackSize||sink.Slot.StackSize>resultCollectible.MaxStackSize)
            throw new InvalidOperationException("Native dropped-item merge violated quantity bounds; merging stopped without replay.");
        return transferred;
    }
    internal void Tick()
    {
        if(disposed||state!="active")return;
        try
        {
            if(runtimeGuard() is string blocked){Stop("blocked",blocked);return;}
            long now=api.World.ElapsedMilliseconds;
            if(now>=nextConflictCheck){nextConflictCheck=now+1000;if(Conflict() is string conflict){Stop("conflict",conflict);return;}}
            cursor??=api.World.LoadedEntities.GetEnumerator();
            long started=Stopwatch.GetTimestamp();int count=0,done=0;
            while(count++<MaxScan&&done<MaxMerges&&Stopwatch.GetElapsedTime(started).TotalMilliseconds<1)
            {
                if(!cursor.MoveNext()){cursor.Dispose();cursor=null;cells.Clear();break;}
                scanned++;
                if(cursor.Current.Value is not EntityItem item||!Eligible(item,now))continue;
                var key=Key(item);
                if(cells.TryGetValue(key,out long id)&&api.World.LoadedEntities.TryGetValue(id,out var candidate)&&candidate is EntityItem other)
                {
                    int quantity=Merge(api.World,item,other,now);
                    if(quantity>0){merges++;moved+=quantity;done++;if(!item.Alive||!other.Alive)removed++;}
                }
                if(cells.Count>=MaxCells)cells.Clear();
                if(Eligible(item,now))cells[key]=item.EntityId;
                else if(cells.TryGetValue(key,out id)&&(!api.World.LoadedEntities.TryGetValue(id,out var old)||old is not EntityItem drop||!Eligible(drop,now)))cells.Remove(key);
            }
        }
        catch(Exception e){failures++;Stop("failed","Item merge stopped: "+e.GetType().Name+". No native operation was replayed; inspect item/mod compatibility.");}
    }
    private void Stop(string status,string message)
    {
        state=status;reason=message;cursor?.Dispose();cursor=null;cells.Clear();
        if(listener>=0){long id=listener;listener=-1;try{api.Event.UnregisterGameTickListener(id);}catch{ /* disposed/state gate keeps a stranded callback inert */ }}
    }
    public void Dispose(){if(disposed)return;disposed=true;Stop("stopped","Dropped-item clumping stopped.");}
}
