using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.Common.Network.Packets;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Only temporary collection construction changes. Stock filtering, packet arrays,
// ordering and send calls remain. Arrays passed to asynchronous senders are never pooled.
public static class PhysicsPacketOptimizations
{
    public const int MaxRetainedCapacity = 4096;
    private static volatile bool enabled;
    private static int generation;
    private static long calls, created, oversized;
    [ThreadStatic] private static Buffers? active;
    [ThreadStatic] private static Buffers? idle;
    public sealed class Buffers
    {
        internal readonly List<Packet_EntityPosition> Positions = new();
        internal readonly List<AnimationPacket> Animations = new();
        internal readonly List<EntityTagPacket> Tags = new();
        internal Buffers? Parent;
        internal int Generation;
    }
    public static MethodInfo Target => AccessTools.Method(typeof(PhysicsManager), nameof(PhysicsManager.SendPositionsAndAnimations));
    public static MethodInfo PrefixMethod => AccessTools.Method(typeof(PhysicsPacketOptimizations), nameof(Prefix));
    public static MethodInfo FinalizerMethod => AccessTools.Method(typeof(PhysicsPacketOptimizations), nameof(Finalizer));
    public static MethodInfo TranspilerMethod => AccessTools.Method(typeof(PhysicsPacketOptimizations), nameof(Transpiler));
    public static (long Calls, long Created, long Oversized) Counters => (Interlocked.Read(ref calls),Interlocked.Read(ref created),Interlocked.Read(ref oversized));
    public static void Configure(bool value)
    {
        enabled = false;
        Interlocked.Increment(ref generation);
        idle = null;
        enabled = value;
    }
    public static bool ValidateContract(out string reason)
    {
        bool valid = Target?.ReturnType == typeof(void) && Target.GetParameters().Select(x=>x.ParameterType).SequenceEqual(new[]{typeof(Dictionary<long,Packet_EntityPosition>),typeof(Dictionary<long,AnimationPacket>),typeof(int),typeof(bool)});
        reason=valid?"Official physics packet list contract verified.":"Physics packet list contract differs.";
        return valid;
    }
    public static void Prefix(out Buffers? __state)
    {
        __state = null;
        if (!enabled && active is null) return;
        var buffers = idle;
        idle = null;
        if (buffers is null || buffers.Generation != generation)
        {
            buffers = new() { Generation = generation };
            Interlocked.Increment(ref created);
        }
        buffers.Parent = active;
        active = __state = buffers;
        Interlocked.Increment(ref calls);
    }
    // Harmony finalizers run on successful completion and exceptions. Never swallow
    // or retry a send failure: duplicate sends would change stock behavior.
    public static Exception? Finalizer(Exception? __exception, Buffers? __state)
    {
        if (__state is null) return __exception;
        var buffers = __state;
        active = buffers.Parent;
        buffers.Parent = null;
        buffers.Positions.Clear(); buffers.Animations.Clear(); buffers.Tags.Clear();
        bool bounded = buffers.Positions.Capacity <= MaxRetainedCapacity && buffers.Animations.Capacity <= MaxRetainedCapacity && buffers.Tags.Capacity <= MaxRetainedCapacity;
        if (!bounded) Interlocked.Increment(ref oversized);
        else if (enabled && buffers.Generation == generation && idle is null) idle = buffers;
        return __exception;
    }
    public static List<Packet_EntityPosition> Positions() => active?.Positions ?? new();
    public static List<AnimationPacket> Animations() => active?.Animations ?? new();
    public static List<EntityTagPacket> Tags() => active?.Tags ?? new();
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
    {
        var code = source.ToList();
        var replacements = new Dictionary<ConstructorInfo,MethodInfo>
        {
            [typeof(List<Packet_EntityPosition>).GetConstructor(Type.EmptyTypes)!] = AccessTools.Method(typeof(PhysicsPacketOptimizations), nameof(Positions)),
            [typeof(List<AnimationPacket>).GetConstructor(Type.EmptyTypes)!] = AccessTools.Method(typeof(PhysicsPacketOptimizations), nameof(Animations)),
            [typeof(List<EntityTagPacket>).GetConstructor(Type.EmptyTypes)!] = AccessTools.Method(typeof(PhysicsPacketOptimizations), nameof(Tags))
        };
        foreach (var pair in replacements)
        {
            var matches = code.Where(x=>x.opcode==OpCodes.Newobj && Equals(x.operand,pair.Key)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Unexpected physics packet list allocation layout; retain stock.");
            matches[0].opcode=OpCodes.Call; matches[0].operand=pair.Value;
        }
        return code;
    }
}
