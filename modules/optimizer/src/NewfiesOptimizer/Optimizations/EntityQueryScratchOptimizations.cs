using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Preserve the official chunk walk, null/tail handling, dimensions, predicate
// order and result ToArray. Only the method-local accumulation list is reused.
public static class EntityQueryScratchOptimizations
{
    public const int MaxCapacity = 4096;
    private static readonly bool[] enabled = new bool[2];
    private static readonly int[] epochs = new int[2];
    private static readonly long[] calls = new long[2], created = new long[2], oversized = new long[2], failures = new long[2];
    [ThreadStatic] private static Frame? active;
    [ThreadStatic] private static Frame?[]? idle;
    public sealed class Frame
    {
        public readonly List<Entity> Entities = new();
        internal Frame? Parent;
        internal int Kind, Epoch;
    }
    public static MethodInfo Target(int kind) => kind switch
    {
        0 => AccessTools.Method(typeof(GameMain), "GetEntitiesAround", [typeof(Vec3d), typeof(float), typeof(float), typeof(ActionConsumable<Entity>)]),
        1 => AccessTools.Method(typeof(GameMain), "GetEntitiesInsideCuboid", [typeof(BlockPos), typeof(BlockPos), typeof(ActionConsumable<Entity>)]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static MethodBase[] Dependencies(int kind) => [Target(kind), typeof(List<Entity>).GetConstructor(Type.EmptyTypes)!, AccessTools.Method(typeof(List<Entity>), nameof(List<Entity>.ToArray))];
    public static bool ValidateContract(int kind, out string reason)
    {
        bool valid = Target(kind)?.DeclaringType == typeof(GameMain) && Target(kind).ReturnType == typeof(Entity[]) && Dependencies(kind).All(m => m != null);
        reason = valid ? "Official entity query accumulation verified; native traversal and owned result array retained." : "Entity query signature differs; retain stock.";
        return valid;
    }
    public static void Configure(int kind, bool value)
    {
        Volatile.Write(ref enabled[kind], false); Interlocked.Increment(ref epochs[kind]);
        if (idle != null) idle[kind] = null;
        Volatile.Write(ref enabled[kind], value);
    }
    public static (long Calls, long Created, long Oversized, long Failures) Counters(int kind) =>
        (Interlocked.Read(ref calls[kind]), Interlocked.Read(ref created[kind]), Interlocked.Read(ref oversized[kind]), Interlocked.Read(ref failures[kind]));
    public static bool CurrentThreadRetainsEntities => idle?.Any(f => f?.Entities.Count > 0 || f?.Parent != null) ?? false;
    private static void Begin(GameMain owner, int kind, out Frame? state)
    {
        state = null;
        // GameMain is shared with the client. NOS only optimizes ServerMain.
        // A disabled nested call gets a barrier so it cannot borrow outer scratch.
        bool use = owner is ServerMain && Volatile.Read(ref enabled[kind]);
        if (!use && active == null) return;
        idle ??= new Frame?[2];
        var frame = idle[kind]; idle[kind] = null;
        int epoch = Volatile.Read(ref epochs[kind]);
        if (frame == null || frame.Epoch != epoch) { frame = new() { Kind = kind, Epoch = epoch }; Interlocked.Increment(ref created[kind]); }
        frame.Parent = active; active = state = frame;
        if (use) Interlocked.Increment(ref calls[kind]);
        else frame.Epoch = -1;
    }
    public static void AroundPrefix(GameMain __instance, out Frame? __state) => Begin(__instance, 0, out __state);
    public static void CuboidPrefix(GameMain __instance, out Frame? __state) => Begin(__instance, 1, out __state);
    public static List<Entity> Scratch() => active is { } f && f.Epoch == Volatile.Read(ref epochs[f.Kind]) && Volatile.Read(ref enabled[f.Kind]) ? f.Entities : new();
    public static Exception? Finalizer(Exception? __exception, Frame? __state)
    {
        if (__state == null) return __exception;
        var f = __state; active = f.Parent; f.Parent = null; f.Entities.Clear();
        bool valid = f.Epoch == Volatile.Read(ref epochs[f.Kind]);
        if (__exception != null && valid) { Interlocked.Increment(ref failures[f.Kind]); Volatile.Write(ref enabled[f.Kind], false); }
        if (f.Entities.Capacity > MaxCapacity) Interlocked.Increment(ref oversized[f.Kind]);
        else if (valid && Volatile.Read(ref enabled[f.Kind]) && idle![f.Kind] == null) idle[f.Kind] = f;
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code = source.ToList(); int constructors = 0, arrays = 0;
        foreach (var c in code)
        {
            if (c.opcode == OpCodes.Newobj && Equals(c.operand, typeof(List<Entity>).GetConstructor(Type.EmptyTypes)))
            { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(EntityQueryScratchOptimizations), nameof(Scratch)); constructors++; }
            if (c.Calls(AccessTools.Method(typeof(List<Entity>), nameof(List<Entity>.ToArray)))) arrays++;
        }
        if (constructors != 1 || arrays != 1) throw new InvalidOperationException("Unexpected entity query accumulation layout; retain stock.");
        return code;
    }
}
