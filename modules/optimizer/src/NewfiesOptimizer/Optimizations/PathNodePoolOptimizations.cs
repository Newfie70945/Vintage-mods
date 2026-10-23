using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Essentials;
namespace NewfiesOptimizer.Optimizations;

// Only waypoint searches on exact official AStar instances borrow candidate nodes.
// FindPath/FindPathOrEscapePath callers outside that scope retain independent nodes.
// Existing open/closed sets and returned waypoint ownership remain unchanged.
public static class PathNodePoolOptimizations
{
    public const int Capacity=4096;
    private static volatile bool scopeEnabled, enabled;
    private static ConditionalWeakTable<AStar,Pool> pools=new();
    [ThreadStatic] private static Lease? active;
    private static long reused,created,overflow,failures;
    public static long Failures=>Interlocked.Read(ref failures);
    private sealed class Pool { public readonly PathNode?[] Nodes=new PathNode?[Capacity]; public int Busy; }
    public sealed class Lease
    {
        internal readonly AStar Owner; private readonly Pool? pool;
        internal readonly Lease? Parent; internal int Next;
        private int reusedCount,createdCount,overflowCount;
        private Lease(AStar owner,Pool? value,Lease? parent){Owner=owner;pool=value;Parent=parent;}
        internal static Lease Begin(AStar owner,Lease? parent)
        {
            var pool=pools.GetValue(owner,static _=>new());
            return new(owner,Interlocked.CompareExchange(ref pool.Busy,1,0)==0?pool:null,parent);
        }
        internal PathNode Candidate(PathNode nearest,Cardinal cardinal)
        {
            if(pool==null||Next>=Capacity){overflowCount++;return new(nearest,cardinal);}
            int slot=Next++;var node=pool.Nodes[slot];
            if(node is null){createdCount++;return pool.Nodes[slot]=new(nearest,cardinal);}
            // The stock core has already cleared openSet/closedSet before reaching
            // its first neighbour constructor. Start/end nodes are never pooled.
            node.X=nearest.X+cardinal.Normali.X;node.Y=nearest.Y+cardinal.Normali.Y;node.Z=nearest.Z+cardinal.Normali.Z;node.dimension=nearest.dimension;
            node.Parent=null!;node.gCost=node.hCost=0;node.pathLength=0;node.HeapIndex=0;node.Action=default;
            reusedCount++;return node;
        }
        internal void End()
        {
            // One lease belongs to one search/thread. Publish counters once,
            // avoiding an atomic operation for every candidate on the hot path.
            if(reusedCount!=0)Interlocked.Add(ref reused,reusedCount);
            if(createdCount!=0)Interlocked.Add(ref created,createdCount);
            if(overflowCount!=0)Interlocked.Add(ref overflow,overflowCount);
            if(pool!=null)Volatile.Write(ref pool.Busy,0);
        }
    }
    public static MethodInfo ScopeTarget=>AccessTools.Method(typeof(AStar),nameof(AStar.FindPathAsWaypoints));
    public static MethodInfo Target=>AccessTools.Method(typeof(AStar),nameof(AStar.FindPathOrEscapePath));
    public static MethodBase[] Dependencies=>[ScopeTarget,Target,AccessTools.Method(typeof(AStar),nameof(AStar.FindPath)),
        AccessTools.Method(typeof(AStar),"ToWaypoints"),AccessTools.Method(typeof(AStar),"retracePath"),AccessTools.Method(typeof(AStar),"traversable"),AccessTools.Method(typeof(AStar),"UpdateNode"),typeof(PathNode).GetConstructor([typeof(PathNode),typeof(Cardinal)])!];
    public static bool ValidateContract(out string reason)
    {
        bool valid=ScopeTarget?.ReturnType==typeof(List<Vec3d>)&&Target?.ReturnType==typeof(List<PathNode>)&&Dependencies.All(x=>x!=null)&&
            typeof(PathNode).GetConstructor([typeof(PathNode),typeof(Cardinal)])!=null;
        reason=valid?"Official waypoint/core node ownership verified; raw node-returning API unpooled.":"Waypoint/node contract differs.";return valid;
    }
    public static void ConfigureScope(bool value)=>scopeEnabled=value;
    public static void Configure(bool value){enabled=value;if(!value)pools=new();}
    public static (long Reused,long Created,long Overflow) Counters=>(Interlocked.Read(ref reused),Interlocked.Read(ref created),Interlocked.Read(ref overflow));
    public static void Prefix(AStar __instance,out Lease? __state)
    {
        __state=null;if(!enabled||!scopeEnabled||__instance.GetType()!=typeof(AStar))return;
        active=__state=Lease.Begin(__instance,active);
    }
    public static Exception? Finalizer(Exception? __exception,Lease? __state)
    {
        if(__state!=null){active=__state.Parent;__state.End();if(__exception!=null)Interlocked.Increment(ref failures);}return __exception;
    }
    public static PathNode Create(PathNode nearest,Cardinal cardinal,AStar owner)=>
        enabled&&scopeEnabled&&active is {} lease&&ReferenceEquals(lease.Owner,owner)?lease.Candidate(nearest,cardinal):new(nearest,cardinal);
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> input)
    {
        var code=input.ToList();var ctor=typeof(PathNode).GetConstructor([typeof(PathNode),typeof(Cardinal)]);int count=0;
        for(int i=0;i<code.Count;i++)if(code[i].opcode==OpCodes.Newobj&&Equals(code[i].operand,ctor))
        {
            var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code[i].labels);load.blocks.AddRange(code[i].blocks);
            code[i]=load;code.Insert(++i,new(OpCodes.Call,AccessTools.Method(typeof(PathNodePoolOptimizations),nameof(Create))));count++;
        }
        if(count!=1)throw new InvalidOperationException("Unexpected neighbour-construction IL; retain stock A*.");return code;
    }
}
