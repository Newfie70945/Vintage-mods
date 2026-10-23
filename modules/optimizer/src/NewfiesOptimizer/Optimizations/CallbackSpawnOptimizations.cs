using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Reuse only private snapshots/scratch. Callback positions and queued spawn
// results keep their original ownership; locks, RNG and decisions remain native.
public static class CallbackSpawnOptimizations
{
    public const int MaxListCapacity = 4096, MaxColumnLength = 256;
    private static readonly bool[] enabled = new bool[4];
    private static readonly int[] epochs = new int[4];
    private static readonly long[] calls = new long[4], created = new long[4], released = new long[4], failures = new long[4], items = new long[4], arraysCreated = new long[4], arraysReused = new long[4];
    [ThreadStatic] private static Frame? active;
    [ThreadStatic] private static Frame?[]? idle;
    public sealed class Frame
    {
        public readonly List<BlockPos> Keys = new();
        public readonly List<SpawnOppurtunity> Opportunities = new();
        public IWorldChunk[]? Column;
        internal Frame? Parent;
        internal int Kind, Epoch;
    }
    public static MethodInfo Target(int kind) => kind switch
    {
        0 => AccessTools.Method(typeof(EventManager), nameof(EventManager.TriggerGameTick), [typeof(long), typeof(IWorldAccessor)]),
        1 => AccessTools.Method(typeof(EventManager), nameof(EventManager.TriggerGameTickDebug), [typeof(long), typeof(IWorldAccessor)]),
        // Both spellings exist in the inspected engine. The inner method's typo
        // is intentional; selecting the wrong overload would violate ownership.
        2 => AccessTools.Method(typeof(ServerSystemEntitySpawner), "TrySpawnSomethingAt_offthrad"),
        3 => AccessTools.Method(typeof(ServerSystemEntitySpawner), "TrySpawnSomethingAt_offthread"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static MethodBase[] Dependencies(int kind) => kind switch
    {
        0 or 1 => [Target(kind), typeof(List<BlockPos>).GetConstructor([typeof(IEnumerable<BlockPos>)])!],
        2 => [Target(2), typeof(List<SpawnOppurtunity>).GetConstructor([typeof(IEnumerable<SpawnOppurtunity>)])!],
        3 => [Target(3), Target(2), AccessTools.Method(typeof(ServerSystemEntitySpawner), "CanSpawnAt_offthread")],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static bool ValidateContract(int kind, out string reason)
    {
        Type[] parameters = kind < 2 ? [typeof(long), typeof(IWorldAccessor)] : kind == 2
            ? [typeof(int), typeof(int), typeof(int), typeof(Dictionary<AssetLocation, int>), typeof(IWorldChunk[]), typeof(List<SpawnState>)]
            : [typeof(int), typeof(int), typeof(int), typeof(Dictionary<AssetLocation, int>), typeof(List<SpawnState>)];
        bool valid = Target(kind)?.ReturnType == typeof(void) && Target(kind).GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters) && Dependencies(kind).All(m => m != null);
        reason = valid ? "Official callback/spawner scratch contracts verified." : "Callback/spawner scratch contract differs.";
        return valid;
    }
    public static (long Calls, long Created, long Released, long Failures, long Items, long ArraysCreated, long ArraysReused) Counters(int kind) =>
        (Interlocked.Read(ref calls[kind]), Interlocked.Read(ref created[kind]), Interlocked.Read(ref released[kind]), Interlocked.Read(ref failures[kind]), Interlocked.Read(ref items[kind]), Interlocked.Read(ref arraysCreated[kind]), Interlocked.Read(ref arraysReused[kind]));
    public static void Configure(int kind, bool value)
    {
        Volatile.Write(ref enabled[kind], false); Interlocked.Increment(ref epochs[kind]);
        if (idle != null) idle[kind] = null;
        Volatile.Write(ref enabled[kind], value);
    }
    private static void Begin(int kind, out Frame? state)
    {
        state = null;
        // A nested disabled call still needs a barrier against the outer scope.
        if (!Volatile.Read(ref enabled[kind]) && active == null) return;
        idle ??= new Frame?[4]; var f = idle[kind]; idle[kind] = null;
        int epoch = Volatile.Read(ref epochs[kind]);
        if (f == null || f.Epoch != epoch) { f = new() { Kind = kind, Epoch = epoch }; Interlocked.Increment(ref created[kind]); }
        f.Parent = active; active = state = f; Interlocked.Increment(ref calls[kind]);
    }
    public static void TickPrefix(out Frame? __state) => Begin(0, out __state);
    public static void DebugPrefix(out Frame? __state) => Begin(1, out __state);
    public static void SpawnPrefix(out Frame? __state) => Begin(2, out __state);
    public static void ColumnPrefix(out Frame? __state) => Begin(3, out __state);
    private static Frame? Current(int kind) => Volatile.Read(ref enabled[kind]) && active is { } f && f.Kind == kind ? f : null;
    private static List<BlockPos> Snapshot(IEnumerable<BlockPos> values, int kind)
    {
        if (values == null || Current(kind) is not { } f) return new(values!);
        f.Keys.Clear(); f.Keys.AddRange(values); Interlocked.Add(ref items[kind], f.Keys.Count);
        return f.Keys;
    }
    public static List<BlockPos> TickSnapshot(IEnumerable<BlockPos> values) => Snapshot(values, 0);
    public static List<BlockPos> DebugSnapshot(IEnumerable<BlockPos> values) => Snapshot(values, 1);
    public static List<SpawnOppurtunity> Opportunities() => Current(2)?.Opportunities ?? new();
    public static IWorldChunk[] Column(int length)
    {
        if (Current(3) is not { } f || length > MaxColumnLength || length < 0) return new IWorldChunk[length];
        if (f.Column == null || f.Column.Length != length) { f.Column = new IWorldChunk[length]; Interlocked.Increment(ref arraysCreated[3]); }
        else Interlocked.Increment(ref arraysReused[3]);
        return f.Column;
    }
    public static Exception? Finalizer(Exception? __exception, Frame? __state)
    {
        if (__state == null) return __exception;
        var f = __state; active = f.Parent; f.Parent = null;
        f.Keys.Clear(); f.Opportunities.Clear();
        if (f.Column != null) Array.Clear(f.Column);
        bool bounded = f.Keys.Capacity <= MaxListCapacity && f.Opportunities.Capacity <= MaxListCapacity && (f.Column?.Length ?? 0) <= MaxColumnLength;
        if (__exception != null) { Interlocked.Increment(ref failures[f.Kind]); Volatile.Write(ref enabled[f.Kind], false); }
        if (!bounded) Interlocked.Increment(ref released[f.Kind]);
        else if (Volatile.Read(ref enabled[f.Kind]) && f.Epoch == Volatile.Read(ref epochs[f.Kind]) && idle![f.Kind] == null) idle[f.Kind] = f;
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
    {
        int kind = Enumerable.Range(0, 4).Single(i => Equals(Target(i), __originalMethod));
        var code = source.ToList(); int snapshots = 0, scratch = 0, ownedCopies = 0, columns = 0;
        foreach (var c in code)
        {
            string? helper = null;
            if (c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ctor)
            {
                if (kind < 2 && Equals(ctor, typeof(List<BlockPos>).GetConstructor([typeof(IEnumerable<BlockPos>)]))) { snapshots++; helper = kind == 0 ? nameof(TickSnapshot) : nameof(DebugSnapshot); }
                if (kind == 2 && ctor.DeclaringType == typeof(List<SpawnOppurtunity>))
                {
                    if (ctor.GetParameters().Length == 0) { scratch++; helper = nameof(Opportunities); }
                    else if (Equals(ctor, typeof(List<SpawnOppurtunity>).GetConstructor([typeof(IEnumerable<SpawnOppurtunity>)]))) ownedCopies++;
                }
            }
            else if (kind == 3 && c.opcode == OpCodes.Newarr && Equals(c.operand, typeof(IWorldChunk))) { columns++; helper = nameof(Column); }
            if (helper != null) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(CallbackSpawnOptimizations), helper); }
        }
        if (kind < 2 ? snapshots != 1 : kind == 2 ? scratch != 1 || ownedCopies != 1 : columns != 1)
            throw new InvalidOperationException("Unexpected callback/spawner allocation layout; retain stock.");
        return code;
    }
}
