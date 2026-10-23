using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Only the method's private staging lists are reused. Queued record objects and
// native save buffers retain their independent ownership. No unloading policy changes.
public static class ChunkUnloadStagingOptimizations
{
    public const int CapacityLimit = 4096;
    private static volatile bool enabled;
    private static int epoch;
    private static long calls, created, released, failures;
    [ThreadStatic] private static Frame? active, idle;
    public sealed class Frame
    {
        public readonly List<ServerChunkWithCoord> Chunks = new();
        public readonly List<ServerMapChunkWithCoord> MapChunks = new();
        internal Frame? Parent;
        internal int Epoch;
        public bool RetainsRecords => Chunks.Count != 0 || MapChunks.Count != 0 || Parent != null;
    }
    public static MethodInfo Target => AccessTools.Method(BlockChunkScratchOptimizations.UnloadOwner, "UnloadChunkColumns");
    public static MethodBase[] Dependencies => [Target, AccessTools.Method(BlockChunkScratchOptimizations.UnloadOwner, "TryUnloadChunk")];
    public static (long Calls, long Created, long Released, long Failures) Counters => (Interlocked.Read(ref calls), Interlocked.Read(ref created), Interlocked.Read(ref released), Interlocked.Read(ref failures));
    public static void Configure(bool value) { enabled = false; Interlocked.Increment(ref epoch); idle = null; enabled = value; }
    public static bool ValidateContract(out string reason)
    {
        try { Transpiler(PatchProcessor.GetOriginalInstructions(Target)).ToArray(); reason = "Official private unload staging lists verified; eligibility, callbacks, locks and save queues retained."; return true; }
        catch { reason = "Unload staging contract differs; retain stock."; return false; }
    }
    public static void Prefix(out Frame? __state)
    {
        __state = null;
        if (!enabled && active == null) return;
        int generation = Volatile.Read(ref epoch);
        var frame = idle; idle = null;
        if (frame == null || frame.Epoch != generation) { frame = new() { Epoch = generation }; Interlocked.Increment(ref created); }
        frame.Parent = active; active = __state = frame;
        Interlocked.Increment(ref calls);
    }
    public static List<ServerChunkWithCoord> Chunks() => enabled && active != null ? active.Chunks : new();
    public static List<ServerMapChunkWithCoord> MapChunks() => enabled && active != null ? active.MapChunks : new();
    public static Exception? Finalizer(Exception? __exception, Frame? __state)
    {
        if (__state == null) return __exception;
        var frame = __state; active = frame.Parent; frame.Parent = null;
        frame.Chunks.Clear(); frame.MapChunks.Clear();
        if (__exception != null) { Interlocked.Increment(ref failures); enabled = false; }
        if (frame.Chunks.Capacity > CapacityLimit || frame.MapChunks.Capacity > CapacityLimit) Interlocked.Increment(ref released);
        else if (enabled && frame.Epoch == Volatile.Read(ref epoch) && idle == null) idle = frame;
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code = source.Select(x => new CodeInstruction(x)).ToList(); int chunks = 0, maps = 0;
        foreach (var instruction in code)
        {
            if (instruction.opcode != OpCodes.Newobj || instruction.operand is not ConstructorInfo ctor || ctor.GetParameters().Length != 0) continue;
            string? helper = null;
            if (ctor.DeclaringType == typeof(List<ServerChunkWithCoord>)) { chunks++; helper = nameof(Chunks); }
            if (ctor.DeclaringType == typeof(List<ServerMapChunkWithCoord>)) { maps++; helper = nameof(MapChunks); }
            if (helper != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ChunkUnloadStagingOptimizations), helper); }
        }
        if (chunks != 1 || maps != 1) throw new InvalidOperationException("Unexpected unload staging layout.");
        return code;
    }
}
