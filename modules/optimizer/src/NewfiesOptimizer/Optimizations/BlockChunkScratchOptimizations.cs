using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

// Only verified method-local scratch is reused. Queued BlockPos objects, chunk
// objects, save buffers and outgoing packet arrays retain their stock ownership.
public static class BlockChunkScratchOptimizations
{
    public const int Capacity = 4096;
    // Nearby-player octagons overlap and may append many duplicate numeric keys.
    // Retain at most 512 KiB here to avoid repeatedly creating a large key array.
    public const int CandidateCapacity = 65536;
    private static readonly bool[] enabled = new bool[4];
    private static readonly int[] epochs = new int[4];
    private static readonly long[] calls = new long[4], created = new long[4], released = new long[4], failures = new long[4], reusedCoordinates = new long[4];
    [ThreadStatic] private static Frame? active;
    [ThreadStatic] private static Frame?[]? idle;

    public sealed class Frame
    {
        public readonly List<BlockPos> Blocks = new(), NoRelight = new(), Decors = new();
        public readonly List<long> Keys = new();
        public readonly List<Vec3i> Coordinates = new(), CoordinatePool = new();
        public readonly Vec2i RequestCoordinate = new();
        internal Frame? Parent;
        internal int Kind, Epoch, Cursor, CoordinateReuses;
    }
    public static Type SupplyOwner => typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemSupplyChunks", true)!;
    public static Type UnloadOwner => typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemUnloadChunks", true)!;
    public static MethodInfo Target(int kind) => kind switch
    {
        0 => AccessTools.Method(typeof(ServerSystemBlockSimulation), "HandleDirtyAndUpdatedBlocks"),
        1 => AccessTools.Method(SupplyOwner, "moveRequestsToGeneratingQueue"),
        2 => AccessTools.Method(UnloadOwner, "FindUnloadableChunkColumnCandidates"),
        3 => AccessTools.Method(UnloadOwner, "SendUnloadedChunkUnloads"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static MethodBase[] Dependencies(int kind) => kind switch
    {
        0 => [Target(0), AccessTools.Method(typeof(ServerMain), "SendSetBlocksPacket"), AccessTools.Method(typeof(ServerMain), "SendSetDecorsPackets"), AccessTools.Method(typeof(ServerWorldMap), "PositionsToUniqueChunks"), AccessTools.Method(typeof(BlockTypeNet), "PackSetBlocksList")],
        1 => [Target(1), AccessTools.Method(typeof(ServerWorldMap), "MapChunkPosFromChunkIndex2D")],
        2 => [Target(2), AccessTools.Method(typeof(ShapeUtil), "LoadOctagonIndices", [typeof(List<long>), typeof(int), typeof(int), typeof(int), typeof(int)])],
        _ => [Target(kind)]
    };
    public static bool ValidateContract(int kind, out string reason)
    {
        bool valid = Target(kind)?.ReturnType == typeof(void) && Target(kind).GetParameters().Length == 0 && Dependencies(kind).All(x => x != null);
        reason = valid ? "Official method-local block/chunk scratch contract verified." : "Block/chunk scratch contract differs.";
        return valid;
    }
    public static (long Calls, long Created, long Released, long Failures, long Coordinates) Counters(int kind) =>
        (Interlocked.Read(ref calls[kind]), Interlocked.Read(ref created[kind]), Interlocked.Read(ref released[kind]), Interlocked.Read(ref failures[kind]), Interlocked.Read(ref reusedCoordinates[kind]));
    public static void Configure(int kind, bool value)
    {
        Volatile.Write(ref enabled[kind], false);
        Interlocked.Increment(ref epochs[kind]);
        if (idle != null) idle[kind] = null;
        Volatile.Write(ref enabled[kind], value);
    }
    private static void Begin(int kind, out Frame? state)
    {
        state = null;
        if (!Volatile.Read(ref enabled[kind]) && active == null) return;
        idle ??= new Frame?[4];
        var f = idle[kind]; idle[kind] = null;
        int epoch = Volatile.Read(ref epochs[kind]);
        if (f == null || f.Epoch != epoch)
        {
            f = new() { Kind = kind, Epoch = epoch };
            Interlocked.Increment(ref created[kind]);
        }
        f.Parent = active; active = state = f;
        Interlocked.Increment(ref calls[kind]);
    }
    public static void BlockPrefix(out Frame? __state) => Begin(0, out __state);
    public static void LoadPrefix(out Frame? __state) => Begin(1, out __state);
    public static void CandidatesPrefix(out Frame? __state) => Begin(2, out __state);
    public static void NotifyPrefix(out Frame? __state) => Begin(3, out __state);
    private static Frame? Current(int kind) => Volatile.Read(ref enabled[kind]) && active is { } f && f.Kind == kind ? f : null;
    public static List<BlockPos> Blocks() => Current(0)?.Blocks ?? new();
    public static List<BlockPos> NoRelight() => Current(0)?.NoRelight ?? new();
    public static List<BlockPos> Decors() => Current(0)?.Decors ?? new();
    public static List<long> LoadKeys() => Current(1)?.Keys ?? new();
    public static List<long> CandidateKeys() => Current(2)?.Keys ?? new();
    public static List<long> NotifyKeys() => Current(3)?.Keys ?? new();
    public static List<Vec3i> Coordinates() => Current(3)?.Coordinates ?? new();
    public static Vec2i RequestCoordinate(ServerWorldMap map, long index)
    {
        if (Current(1) is not { } f) return map.MapChunkPosFromChunkIndex2D(index);
        // The original caller reads X/Y into integer arguments immediately. This
        // object is never handed to generation requests, plugins or the database.
        f.RequestCoordinate.X = (int)(index % map.ChunkMapSizeX);
        f.RequestCoordinate.Y = (int)(index / map.ChunkMapSizeX);
        f.CoordinateReuses++;
        return f.RequestCoordinate;
    }
    // The original loop clears its coordinate list for each recipient. Coordinates
    // have already been copied into newly owned int[] arrays before the next clear.
    public static void ClearCoordinates(List<Vec3i> list)
    {
        list.Clear();
        if (Current(3) is { } f && ReferenceEquals(list, f.Coordinates)) f.Cursor = 0;
    }
    public static Vec3i Coordinate(int x, int y, int z)
    {
        if (Current(3) is not { } f || f.Cursor >= Capacity) return new(x, y, z);
        Vec3i value;
        if (f.Cursor < f.CoordinatePool.Count) { value = f.CoordinatePool[f.Cursor]; f.CoordinateReuses++; }
        else { value = new(); f.CoordinatePool.Add(value); }
        f.Cursor++; value.X = x; value.Y = y; value.Z = z;
        return value;
    }
    public static Exception? Finalizer(Exception? __exception, Frame? __state)
    {
        if (__state == null) return __exception;
        var f = __state; active = f.Parent; f.Parent = null;
        if (f.CoordinateReuses != 0) { Interlocked.Add(ref reusedCoordinates[f.Kind], f.CoordinateReuses); f.CoordinateReuses = 0; }
        f.Blocks.Clear(); f.NoRelight.Clear(); f.Decors.Clear(); f.Keys.Clear(); f.Coordinates.Clear(); f.Cursor = 0;
        bool bounded = f.Blocks.Capacity <= Capacity && f.NoRelight.Capacity <= Capacity && f.Decors.Capacity <= Capacity && f.Keys.Capacity <= (f.Kind == 2 ? CandidateCapacity : Capacity) && f.Coordinates.Capacity <= Capacity && f.CoordinatePool.Capacity <= Capacity;
        if (__exception != null)
        {
            // Preserve the original exception and never replay partially completed
            // queue processing or sends. The registry removes this target next tick.
            Interlocked.Increment(ref failures[f.Kind]); Volatile.Write(ref enabled[f.Kind], false);
        }
        if (!bounded) { f.CoordinatePool.Clear(); Interlocked.Increment(ref released[f.Kind]); }
        else if (Volatile.Read(ref enabled[f.Kind]) && f.Epoch == Volatile.Read(ref epochs[f.Kind]) && idle![f.Kind] == null) idle[f.Kind] = f;
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
    {
        int kind = Enumerable.Range(0, 4).Single(i => Equals(Target(i), __originalMethod));
        var code = source.ToList(); int blocks = 0, keys = 0, coordinates = 0, vectors = 0, clears = 0, requestCoordinates = 0;
        foreach (var c in code)
        {
            string? helper = null;
            if (c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo ctor)
            {
                if (kind == 0 && ctor.DeclaringType == typeof(List<BlockPos>) && ctor.GetParameters().Length == 0)
                    helper = blocks++ switch { 0 => nameof(Blocks), 1 => nameof(NoRelight), 2 => nameof(Decors), _ => throw new InvalidOperationException("Unexpected block list allocation.") };
                else if (kind != 0 && ctor.DeclaringType == typeof(List<long>) && ctor.GetParameters().Length == 0)
                { keys++; helper = kind switch { 1 => nameof(LoadKeys), 2 => nameof(CandidateKeys), _ => nameof(NotifyKeys) }; }
                else if (kind == 3 && ctor.DeclaringType == typeof(List<Vec3i>) && ctor.GetParameters().Length == 0)
                { coordinates++; helper = nameof(Coordinates); }
                else if (kind == 3 && Equals(ctor, typeof(Vec3i).GetConstructor([typeof(int), typeof(int), typeof(int)])))
                { vectors++; helper = nameof(Coordinate); }
            }
            else if (kind == 3 && c.Calls(AccessTools.Method(typeof(List<Vec3i>), nameof(List<Vec3i>.Clear))))
            { clears++; helper = nameof(ClearCoordinates); }
            else if (kind == 1 && c.Calls(AccessTools.Method(typeof(ServerWorldMap), "MapChunkPosFromChunkIndex2D")))
            { requestCoordinates++; helper = nameof(RequestCoordinate); }
            if (helper != null) { c.opcode = OpCodes.Call; c.operand = AccessTools.Method(typeof(BlockChunkScratchOptimizations), helper); }
        }
        if (kind == 0 ? blocks != 3 : keys != 1 || (kind == 1 && requestCoordinates != 1) || (kind == 3 && (coordinates != 1 || vectors != 1 || clears != 1)))
            throw new InvalidOperationException("Unexpected block/chunk scratch IL; retain stock.");
        return code;
    }
}
