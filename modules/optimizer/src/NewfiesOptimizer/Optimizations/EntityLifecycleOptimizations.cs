using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Server;
namespace NewfiesOptimizer.Optimizations;

// These lists are method-local in stock code. They never escape into mods or
// packets: the stock ToArray calls still allocate independently owned outputs.
public static class EntityLifecycleOptimizations
{
    public const int Capacity=4096;
    private static volatile bool ticks,despawns;
    private static int tickEpoch,despawnEpoch;
    private static long calls,created,released,failures;
    [ThreadStatic]private static Frame? active;
    [ThreadStatic]private static Frame? idleTick;
    [ThreadStatic]private static Frame? idleDespawn;
    public sealed class Frame
    {
        public readonly List<KeyValuePair<Entity,EntityDespawnData>> Removed=new();
        public readonly List<long> Ids=new();public readonly List<int> Reasons=new(),Sources=new();
        internal Frame? Parent;internal int Kind,Epoch;
    }
    public static Type Owner=>typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemEntitySimulation",true)!;
    public static MethodInfo TickTarget=>AccessTools.Method(Owner,"TickEntities");
    public static MethodInfo DespawnTarget=>AccessTools.Method(Owner,"SendEntityDespawns");
    public static MethodInfo[] Dependencies(int kind)=>kind==0?[TickTarget]:[DespawnTarget];
    public static void ConfigureTicks(bool value){ticks=false;Interlocked.Increment(ref tickEpoch);idleTick=null;ticks=value;}
    public static void ConfigureDespawns(bool value){despawns=false;Interlocked.Increment(ref despawnEpoch);idleDespawn=null;despawns=value;}
    public static (long Calls,long Created,long Released,long Failures) Counters=>(Interlocked.Read(ref calls),Interlocked.Read(ref created),Interlocked.Read(ref released),Interlocked.Read(ref failures));
    public static bool ValidateContract(out string reason)
    {bool valid=TickTarget?.ReturnType==typeof(void)&&DespawnTarget?.ReturnType==typeof(void)&&AccessTools.Field(Owner,"server")?.FieldType==typeof(ServerMain);reason=valid?"Official entity cleanup/despawn local-list contracts verified.":"Entity lifecycle contract differs.";return valid;}
    private static void Begin(int kind,out Frame? state)
    {
        state=null;if(!(kind==0?ticks:despawns))return;
        var frame=kind==0?idleTick:idleDespawn;if(kind==0)idleTick=null;else idleDespawn=null;
        int epoch=kind==0?tickEpoch:despawnEpoch;
        if(frame==null||frame.Epoch!=epoch){frame=new(){Kind=kind,Epoch=epoch};Interlocked.Increment(ref created);}
        frame.Parent=active;active=state=frame;Interlocked.Increment(ref calls);
    }
    public static void TickPrefix(out Frame? __state)=>Begin(0,out __state);
    public static void DespawnPrefix(out Frame? __state)=>Begin(1,out __state);
    public static List<KeyValuePair<Entity,EntityDespawnData>> Removed()=>ticks&&active is {Kind:0} f?f.Removed:new();
    public static List<long> Ids()=>despawns&&active is {Kind:1} f?f.Ids:new();
    public static List<int> Reasons()=>despawns&&active is {Kind:1} f?f.Reasons:new();
    public static List<int> Sources()=>despawns&&active is {Kind:1} f?f.Sources:new();
    public static Exception? Finalizer(Exception? __exception,Frame? __state)
    {
        if(__state==null)return __exception;
        var f=__state;active=f.Parent;f.Parent=null;
        f.Removed.Clear();f.Ids.Clear();f.Reasons.Clear();f.Sources.Clear();
        bool fits=f.Removed.Capacity<=Capacity&&f.Ids.Capacity<=Capacity&&f.Reasons.Capacity<=Capacity&&f.Sources.Capacity<=Capacity;
        if(!fits)Interlocked.Increment(ref released);
        else if(f.Kind==0&&ticks&&f.Epoch==tickEpoch&&idleTick==null)idleTick=f;
        else if(f.Kind==1&&despawns&&f.Epoch==despawnEpoch&&idleDespawn==null)idleDespawn=f;
        if(__exception!=null)Interlocked.Increment(ref failures);
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input,MethodBase __originalMethod)
    {
        var code=input.ToList();bool tick=__originalMethod==TickTarget;int removed=0,ids=0,ints=0;
        foreach(var instruction in code)
        {
            if(instruction.opcode!=OpCodes.Newobj||instruction.operand is not ConstructorInfo ctor||ctor.GetParameters().Length!=0)continue;
            string? helper=null;
            if(tick&&ctor.DeclaringType==typeof(List<KeyValuePair<Entity,EntityDespawnData>>)){removed++;helper=nameof(Removed);}
            else if(!tick&&ctor.DeclaringType==typeof(List<long>)){ids++;helper=nameof(Ids);}
            else if(!tick&&ctor.DeclaringType==typeof(List<int>)){helper=ints++==0?nameof(Reasons):nameof(Sources);}
            if(helper!=null){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(EntityLifecycleOptimizations),helper);}
        }
        if(tick?(removed!=1||ids!=0||ints!=0):(removed!=0||ids!=1||ints!=2))throw new InvalidOperationException("Unexpected lifecycle list IL; retain stock.");
        return code;
    }
}
