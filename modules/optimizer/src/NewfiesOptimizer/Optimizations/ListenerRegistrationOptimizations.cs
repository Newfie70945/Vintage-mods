using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace NewfiesOptimizer.Optimizations;

// Index only the first-hole search. Native code still constructs the listener,
// assigns its ID, writes the slot and updates the authoritative ID dictionary.
public static class ListenerRegistrationOptimizations
{
    public const int MaxSlots=65536;
    private static readonly bool[] enabled=new bool[2];
    private static readonly long[] pathFailures=new long[2];
    private static ConditionalWeakTable<object, Index> cache=new();
    private static long calls,rebuilds,avoided,fallbacks,failures;
    internal sealed class Index { internal readonly List<int> Holes=new(); internal int Cursor,Version,Count; }
    private static class Version<T> { internal static readonly AccessTools.FieldRef<List<T>,int> Read=AccessTools.FieldRefAccess<List<T>,int>("_version"); }
    public struct Scope { internal bool Active; internal object? List; internal Index? Index; internal int Kind,Slot,Count,Version; }
    [ThreadStatic]private static Scope current;
    public static MethodInfo Target(int kind)=>kind==0
        ?AccessTools.Method(typeof(EventManager),nameof(EventManager.AddGameTickListener),[typeof(Action<float>),typeof(Action<Exception>),typeof(int),typeof(int)])
        :AccessTools.Method(typeof(EventManager),"AddGameTickListenerBlockInternal");
    public static MethodBase[] Dependencies(int kind)
    {
        var type=kind==0?typeof(List<GameTickListener>):typeof(List<GameTickListenerBlock>);
        return [Target(kind),AccessTools.PropertyGetter(type,"Item"),AccessTools.PropertyGetter(type,"Count")];
    }
    public static void Configure(int kind,bool value){Volatile.Write(ref enabled[kind],false);Interlocked.Exchange(ref cache,new());Volatile.Write(ref enabled[kind],value);}
    public static long Failures(int kind)=>Interlocked.Read(ref pathFailures[kind]);
    public static (long Calls,long Rebuilds,long Avoided,long Fallbacks,long Failures) Counters=>(Interlocked.Read(ref calls),Interlocked.Read(ref rebuilds),Interlocked.Read(ref avoided),Interlocked.Read(ref fallbacks),Interlocked.Read(ref failures));
    public static bool ValidateContract(int kind,out string reason)
    {
        try
        {
            _=Version<GameTickListener>.Read;_=Version<GameTickListenerBlock>.Read;
            Transpiler(PatchProcessor.GetOriginalInstructions(Target(kind)),Target(kind)).ToArray();
            reason="Official first-free-slot registration loop and runtime list-version guard verified.";return true;
        }
        catch {reason="Listener registration layout differs; retain stock.";return false;}
    }
    private static void Begin(int kind,out Scope state){state=current;current=new(){Kind=kind,Active=Volatile.Read(ref enabled[kind])&&Environment.CurrentManagedThreadId==RuntimeEnv.ServerMainThreadId};}
    public static void EntityPrefix(out Scope __state)=>Begin(0,out __state);
    public static void BlockPrefix(out Scope __state)=>Begin(1,out __state);
    public static int EntityHole(List<GameTickListener> list)=>Find(list,0);
    public static int BlockHole(List<GameTickListenerBlock> list)=>Find(list,1);
    private static int Find<T>(List<T> list,int kind) where T:class
    {
        if(!Volatile.Read(ref enabled[kind])||!current.Active)return 0;
        if(list.Count>MaxSlots||list.GetType()!=typeof(List<T>)){Interlocked.Increment(ref fallbacks);return 0;}
        var index=cache.GetValue(list,static _=>new());int version=Version<T>.Read(list);
        if(index.Version!=version||index.Count!=list.Count)
        {
            index.Holes.Clear();index.Cursor=0;
            for(int i=0;i<list.Count;i++)if(list[i]==null)index.Holes.Add(i);
            index.Version=version;index.Count=list.Count;Interlocked.Increment(ref rebuilds);
        }
        int slot=index.Cursor<index.Holes.Count?index.Holes[index.Cursor]:list.Count;
        // A surprising candidate is never used to overwrite a live listener.
        if(slot<list.Count&&list[slot]!=null){Interlocked.Increment(ref fallbacks);index.Version=version-1;return 0;}
        current.List=list;current.Index=index;current.Kind=kind;current.Slot=slot;current.Count=list.Count;current.Version=version;
        Interlocked.Increment(ref calls);Interlocked.Add(ref avoided,slot);
        return slot;
    }
    private static void Complete<T>(List<T> list,Index index) where T:class
    {
        int expectedCount=current.Count+(current.Slot==current.Count?1:0);
        // Publish the next index only for the native single slot write/append.
        // Unexpected mutation forces a rebuild on the next registration.
        int version=Version<T>.Read(list);
        if(version!=unchecked(current.Version+1)||list.Count!=expectedCount||current.Slot>=list.Count||list[current.Slot]==null){index.Version=version-1;return;}
        if(current.Slot<current.Count)index.Cursor++;
        index.Version=version;index.Count=list.Count;
    }
    public static Exception? Finalizer(Exception? __exception,Scope __state)
    {
        try
        {
            if(current.Index is {} index&&current.List is {} list)
            {
                if(__exception==null){if(current.Kind==0)Complete((List<GameTickListener>)list,index);else Complete((List<GameTickListenerBlock>)list,index);}
                else {index.Version--;Volatile.Write(ref enabled[current.Kind],false);Interlocked.Increment(ref pathFailures[current.Kind]);Interlocked.Increment(ref failures);}
            }
        }
        finally {current=__state;}
        return __exception;
    }
    private static int? Stored(CodeInstruction c)=>c.opcode==OpCodes.Stloc_0?0:c.opcode==OpCodes.Stloc_1?1:c.opcode==OpCodes.Stloc_2?2:c.opcode==OpCodes.Stloc_3?3:c.opcode==OpCodes.Stloc||c.opcode==OpCodes.Stloc_S?c.operand is LocalBuilder b?b.LocalIndex:Convert.ToInt32(c.operand):null;
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source,MethodBase __originalMethod)
    {
        bool entity=Equals(__originalMethod,Target(0));var type=entity?typeof(List<GameTickListener>):typeof(List<GameTickListenerBlock>);
        var locals=__originalMethod.GetMethodBody()!.LocalVariables;var code=source.Select(c=>new CodeInstruction(c)).ToList();int found=0;
        for(int i=1;i<code.Count-1;i++)
        {
            if(code[i].opcode!=OpCodes.Ldc_I4_0||Stored(code[i-1]) is not int list||locals[list].LocalType!=type||Stored(code[i+1]) is not int loop||locals[loop].LocalType!=typeof(int))continue;
            var load=new CodeInstruction(OpCodes.Ldloc,(short)list);load.labels.AddRange(code[i].labels);load.blocks.AddRange(code[i].blocks);
            code[i]=load;code.Insert(++i,new(OpCodes.Call,AccessTools.Method(typeof(ListenerRegistrationOptimizations),entity?nameof(EntityHole):nameof(BlockHole))));found++;
        }
        if(found!=1)throw new InvalidOperationException("Unexpected first-free listener loop; retain stock.");
        return code;
    }
}
