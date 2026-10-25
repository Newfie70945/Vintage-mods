using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Diagnostics only: never defer, retry or omit lifecycle cleanup.
public static class DespawnCleanupTimings
{
    private static volatile bool enabled;
    private static long calls,failures;
    private sealed class Label(Type type) { internal readonly string Text="nos-despawn-behavior:"+type.FullName; }
    private static readonly ConditionalWeakTable<Type,Label> labels=new();
    public static MethodInfo Target=>AccessTools.Method(typeof(Entity),nameof(Entity.OnEntityDespawn));
    public static MethodBase[] Dependencies=>[Target];
    public static (long Calls,long Failures) Counters=>(Interlocked.Read(ref calls),Interlocked.Read(ref failures));
    public static void Configure(bool value)=>enabled=value;
    public static bool ValidateContract(out string reason)
    {
        try {Transpiler(PatchProcessor.GetOriginalInstructions(Target)).ToArray();reason="Native despawn behavior/dispose call sites verified; cleanup order and exceptions retained.";return true;}
        catch {reason="Native despawn body differs; retain stock.";return false;}
    }
    private static bool Trace=>enabled&&Environment.CurrentManagedThreadId==RuntimeEnv.ServerMainThreadId&&ServerMain.FrameProfiler?.Enabled==true;
    public static void Behavior(EntityBehavior behavior,EntityDespawnData data)
    {
        if(!Trace){behavior.OnEntityDespawn(data);return;}
        ServerMain.FrameProfiler.Enter(labels.GetValue(behavior.GetType(),static t=>new Label(t)).Text);
        try {Interlocked.Increment(ref calls);behavior.OnEntityDespawn(data);}
        finally {ServerMain.FrameProfiler.Leave();}
    }
    public static void Animation(IAnimationManager manager)
    {
        if(!Trace){manager.Dispose();return;}
        ServerMain.FrameProfiler.Enter("nos-despawn-animation-dispose");
        try {Interlocked.Increment(ref calls);manager.Dispose();}
        finally {ServerMain.FrameProfiler.Leave();}
    }
    public static Exception? Finalizer(Exception? __exception)
    {
        if(__exception!=null&&enabled){enabled=false;Interlocked.Increment(ref failures);}
        return __exception; // Preserve the native failure; never replay partially completed cleanup.
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code=source.Select(c=>new CodeInstruction(c)).ToList();int behavior=0,dispose=0;
        for(int i=0;i<code.Count;i++)
        {
            var c=code[i];
            if(c.operand is not MethodInfo m||c.opcode!=OpCodes.Callvirt)continue;
            string? helper=null;
            if(m==AccessTools.Method(typeof(EntityBehavior),nameof(EntityBehavior.OnEntityDespawn))){behavior++;helper=nameof(Behavior);}
            else if(m.Name==nameof(IDisposable.Dispose)&&m.GetParameters().Length==0&&i>0&&Equals(code[i-1].operand,AccessTools.PropertyGetter(typeof(Entity),nameof(Entity.AnimManager)))){dispose++;helper=nameof(Animation);}
            if(helper!=null){c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(DespawnCleanupTimings),helper);}
        }
        if(behavior!=1||dispose!=1)throw new InvalidOperationException("Unexpected despawn cleanup call sites.");
        return code;
    }
}
