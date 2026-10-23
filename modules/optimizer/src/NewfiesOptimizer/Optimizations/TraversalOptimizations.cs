using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace NewfiesOptimizer.Optimizations;

// Change only consumer-local traversal. No iterator/slot/geometry pool, cached
// transition result or skipped collision work. Custom enumerators stay native.
public static class TraversalOptimizations
{
    private static readonly bool[] enabled = new bool[3];
    private static readonly long[] calls = new long[3], fallbacks = new long[3], failures = new long[3];
    private sealed class InventoryContract(bool native) { public readonly bool Native = native; }
    private static readonly ConditionalWeakTable<Type, InventoryContract> contracts = new();
    private static readonly ConditionalWeakTable<Type, InventoryContract>.CreateValueCallback InspectInventory = type =>
        new(AccessTools.Method(type, nameof(InventoryBase.GetEnumerator), Type.EmptyTypes)?.DeclaringType == typeof(InventoryBase));
    public static MethodInfo Target(int kind) => kind switch
    {
        0 => AccessTools.Method(typeof(CollisionTester), nameof(CollisionTester.ApplyTerrainCollision)),
        1 => AccessTools.Method(typeof(InWorldContainer), "HasTransitionables"),
        2 => AccessTools.Method(typeof(InWorldContainer), nameof(InWorldContainer.OnTick), [typeof(float)]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static MethodInfo Factory(int kind) => AccessTools.Method(kind == 0 ? typeof(CachedCuboidListFaster) : typeof(InventoryBase), "GetEnumerator", Type.EmptyTypes);
    public static MethodBase[] Dependencies(int kind)
    {
        var factory = Factory(kind);
        var state = factory.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
        return new MethodBase[] { Target(kind), factory }.Concat(state == null ? [] : state.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(state.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))).ToArray();
    }
    public static bool ValidateContract(int kind, out string reason)
    {
        var target = Target(kind); var factory = Factory(kind);
        bool valid = target?.ReturnType == (kind == 1 ? typeof(bool) : typeof(void)) && factory?.ReturnType == (kind == 0 ? typeof(IEnumerator<Cuboidd>) : typeof(IEnumerator<ItemSlot>)) && factory.GetCustomAttribute<IteratorStateMachineAttribute>() != null;
        reason = valid ? "Official scoped traversal verified; native body, hooks and custom inventory enumerators retained." : "Scoped traversal differs; retain stock.";
        return valid;
    }
    public static void Configure(int kind, bool value) => Volatile.Write(ref enabled[kind], value);
    public static (long Calls, long Fallbacks, long Failures) Counters(int kind) =>
        (Interlocked.Read(ref calls[kind]), Interlocked.Read(ref fallbacks[kind]), Interlocked.Read(ref failures[kind]));
    public static void CollisionPrefix(out bool __state) => __state = Volatile.Read(ref enabled[0]);
    public static void ScanPrefix(out bool __state) => __state = Volatile.Read(ref enabled[1]);
    public static void TickPrefix(out bool __state) => __state = Volatile.Read(ref enabled[2]);
    public static BoxIterator Boxes(CachedCuboidListFaster source)
    {
        if (!Volatile.Read(ref enabled[0]) || source == null) return new() { Fallback = source!.GetEnumerator(), Custom = true };
        Interlocked.Increment(ref calls[0]); return new() { Source = source, Index = -1 };
    }
    public static SlotIterator ScanSlots(InventoryBase source) => Slots(source, 1);
    public static SlotIterator TickSlots(InventoryBase source) => Slots(source, 2);
    private static SlotIterator Slots(InventoryBase source, int kind)
    {
        if (!Volatile.Read(ref enabled[kind]) || source == null) return new() { Fallback = source!.GetEnumerator(), Custom = true };
        if (!contracts.GetValue(source.GetType(), InspectInventory).Native)
        { Interlocked.Increment(ref fallbacks[kind]); return new() { Fallback = source.GetEnumerator(), Custom = true }; }
        Interlocked.Increment(ref calls[kind]); return new() { Source = source, Index = -1 };
    }
    public struct BoxIterator
    {
        internal CachedCuboidListFaster? Source; internal IEnumerator<Cuboidd>? Fallback; internal int Index; internal bool Finished, Custom;
        public Cuboidd Current { get; private set; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (Custom) { if (Fallback!.MoveNext()) { Current = Fallback.Current; return true; } return false; }
            if (Finished) return false;
            if (++Index < Source!.Count) { Current = Source.cuboids[Index]; return true; }
            Finished = true; return false;
        }
        public void Dispose() { try { Fallback?.Dispose(); } finally { Source = null; Fallback = null; Current = null!; Finished = true; } }
        public readonly bool RetainsInputs => Source != null || Fallback != null || Current != null;
    }
    public struct SlotIterator
    {
        internal InventoryBase? Source; internal IEnumerator<ItemSlot>? Fallback; internal int Index; internal bool Finished, Custom;
        public ItemSlot Current { get; private set; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (Custom) { if (Fallback!.MoveNext()) { Current = Fallback.Current; return true; } return false; }
            if (Finished) return false;
            if (++Index < Source!.Count) { Current = Source[Index]; return true; }
            Finished = true; return false;
        }
        public void Dispose() { try { Fallback?.Dispose(); } finally { Source = null; Fallback = null; Current = null!; Finished = true; } }
        public readonly bool RetainsInputs => Source != null || Fallback != null || Current != null;
    }
    public static Exception? CollisionFinalizer(Exception? __exception, bool __state) => Finish(0, __exception, __state);
    public static Exception? ScanFinalizer(Exception? __exception, bool __state) => Finish(1, __exception, __state);
    public static Exception? TickFinalizer(Exception? __exception, bool __state) => Finish(2, __exception, __state);
    private static Exception? Finish(int kind, Exception? error, bool executed)
    {
        if (error != null && executed) { Interlocked.Increment(ref failures[kind]); Volatile.Write(ref enabled[kind], false); }
        return error; // Never replay collision hooks or a partial food transition.
    }
    private static int? LocalIndex(CodeInstruction c)
    {
        if (c.opcode == OpCodes.Ldloc_0 || c.opcode == OpCodes.Stloc_0) return 0;
        if (c.opcode == OpCodes.Ldloc_1 || c.opcode == OpCodes.Stloc_1) return 1;
        if (c.opcode == OpCodes.Ldloc_2 || c.opcode == OpCodes.Stloc_2) return 2;
        if (c.opcode == OpCodes.Ldloc_3 || c.opcode == OpCodes.Stloc_3) return 3;
        return c.operand is LocalBuilder local ? local.LocalIndex : c.operand is int i ? i : c.operand is byte b ? b : c.operand is sbyte s ? s : null;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, MethodBase __originalMethod, ILGenerator generator)
    {
        int kind = Enumerable.Range(0, 3).Single(i => Equals(Target(i), __originalMethod));
        var code = source.ToList(); var factories = code.Select((c,i)=>(c,i)).Where(p=>p.c.Calls(Factory(kind))).ToArray();
        if (factories.Length != 1) throw new InvalidOperationException("Unexpected traversal factory layout; retain stock.");
        int at = factories[0].i, store = at + 1;
        while (store < code.Count && code[store].opcode == OpCodes.Nop) store++;
        if (store >= code.Count || !code[store].IsStloc() || LocalIndex(code[store]) is not int oldLocal) throw new InvalidOperationException("Unexpected traversal local; retain stock.");
        Type iteratorType = kind == 0 ? typeof(BoxIterator) : typeof(SlotIterator);
        var local = generator.DeclareLocal(iteratorType);
        code[at].opcode = OpCodes.Call; code[at].operand = AccessTools.Method(typeof(TraversalOptimizations), kind == 0 ? nameof(Boxes) : kind == 1 ? nameof(ScanSlots) : nameof(TickSlots));
        code[store].opcode = OpCodes.Stloc; code[store].operand = local;
        int current = 0, move = 0, dispose = 0, guard = 0;
        for (int i = store + 1; i < code.Count; i++)
        {
            if (!code[i].IsLdloc() || LocalIndex(code[i]) != oldLocal) continue;
            int next = i + 1; while (next < code.Count && code[next].opcode == OpCodes.Nop) next++;
            if (next >= code.Count) throw new InvalidOperationException("Unexpected traversal consumer.");
            var consumer = code[next];
            if (consumer.opcode == OpCodes.Brfalse || consumer.opcode == OpCodes.Brfalse_S)
            { code[i].opcode = OpCodes.Ldc_I4_1; code[i].operand = null; guard++; continue; }
            string? name = consumer.operand is MethodInfo m ? m.Name : null;
            string method = name switch { "get_Current" => "get_Current", "MoveNext" => "MoveNext", "Dispose" => "Dispose", _ => throw new InvalidOperationException("Unexpected traversal consumer; retain stock.") };
            if (method == "get_Current") current++; else if (method == "MoveNext") move++; else dispose++;
            code[i].opcode = OpCodes.Ldloca; code[i].operand = local;
            consumer.opcode = OpCodes.Call; consumer.operand = AccessTools.Method(iteratorType, method);
        }
        if (current != 1 || move != 1 || dispose != 1 || guard != 1) throw new InvalidOperationException("Unexpected traversal loop/finally layout; retain stock.");
        return code;
    }
}
