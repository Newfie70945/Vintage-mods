using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.Server;
namespace NewfiesOptimizer.Optimizations;

// Optional scheduling policy. The native body still owns dispatch, catch/log,
// count limit and queue order. Nothing is removed from the queue by this patch.
public static class BlockSimulationBudget
{
    private static volatile bool enabled;
    private static int budgetMs=5;
    private static int pressureFallback;
    public static bool PressureFallback => Volatile.Read(ref pressureFallback)!=0;
    private static long deferred, failures, passes, queueDepth, peakQueue;
    public const int HighWater=8192;
    [ThreadStatic] private static Scope current;
    public struct Scope { internal bool Active, Deferred; internal long Start; internal int Checks; }
    public static MethodInfo Target=>AccessTools.Method(typeof(ServerSystemBlockSimulation),nameof(ServerSystemBlockSimulation.OnServerTick));
    public static MethodInfo Empty=>AccessTools.PropertyGetter(typeof(ConcurrentQueue<object>),nameof(ConcurrentQueue<object>.IsEmpty));
    public static MethodBase[] Dependencies=>[Target,Empty,AccessTools.Method(typeof(ConcurrentQueue<object>),nameof(ConcurrentQueue<object>.TryDequeue))];
    public static void Configure(bool value){if(value)Volatile.Write(ref pressureFallback,0);enabled=value;}
    public static void SetBudget(int value)=>Volatile.Write(ref budgetMs,Math.Clamp(value,1,20));
    public static (long Passes,long Deferred,long Failures,long Queue,long Peak) Counters=>(Interlocked.Read(ref passes),Interlocked.Read(ref deferred),Interlocked.Read(ref failures),Interlocked.Read(ref queueDepth),Interlocked.Read(ref peakQueue));
    public static bool ValidateContract(out string reason)
    {
        var code=PatchProcessor.GetOriginalInstructions(Target);
        bool valid=Target.GetMethodBody()?.GetILAsByteArray() is {} il && Convert.ToHexString(SHA256.HashData(il))=="17CB5B1E42F33A8278B6867523F16A62678B9DD80B6EA31E2CE525DFF1A5654E" && Target.ReturnType==typeof(void)&&Target.GetParameters().Length==1&&Target.GetParameters()[0].ParameterType==typeof(float)&&code.Count(x=>x.Calls(Empty))==1&&code.Count(x=>x.Calls((MethodInfo)Dependencies[2]))==1;
        reason=valid?"Native queued block tick loop verified; FIFO dispatch/catch/count limit retained.":"Block tick loop differs; retain stock.";
        return valid;
    }
    public static void Prefix(out Scope __state)
    {
        __state=current;
        current=new Scope{Active=enabled,Start=Stopwatch.GetTimestamp()};
        if(current.Active)Interlocked.Increment(ref passes);
    }
    public static bool ShouldStop(ConcurrentQueue<object> queue)
    {
        // Retain native null behavior and actual emptiness, including concurrent producers.
        if(queue.IsEmpty){if(current.Active)Interlocked.Exchange(ref queueDepth,0);return true;}
        if(!enabled||!current.Active)return false;
        if(current.Checks++==0)
        {
            int count=queue.Count;Interlocked.Exchange(ref queueDepth,count);
            long old=Interlocked.Read(ref peakQueue);while(count>old){long seen=Interlocked.CompareExchange(ref peakQueue,count,old);if(seen==old)break;old=seen;}
            // Scheduling must not silently accumulate an unlimited backlog.
            // On pressure, keep all pending work and fail back to stock cadence.
            if(count>HighWater){Volatile.Write(ref pressureFallback,1);enabled=false;current.Active=false;Interlocked.Increment(ref failures);}
            return false; // at least one native dispatch may progress
        }
        if(Stopwatch.GetElapsedTime(current.Start).TotalMilliseconds<Volatile.Read(ref budgetMs))return false;
        if(!current.Deferred){current.Deferred=true;Interlocked.Increment(ref deferred);}
        return true;
    }
    public static Exception? Finalizer(Exception? __exception,Scope __state)
    {
        if(__exception!=null&&current.Active){enabled=false;Interlocked.Increment(ref failures);}
        current=__state;return __exception;
    }
    public static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> source)
    {
        var code=source.ToList();int hits=0;
        foreach(var instruction in code)if(instruction.Calls(Empty)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(BlockSimulationBudget),nameof(ShouldStop));hits++;}
        if(hits!=1)throw new InvalidOperationException("Unexpected block tick loop; retain stock.");
        return code;
    }
}
