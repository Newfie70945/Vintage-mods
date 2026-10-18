using System.Threading;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace NewfiesOptimizer.Optimizations;

/// <summary>
/// Allocation-only replacements for the inspected official 1.22.7 player queries.
/// The owner must verify the engine and foreign patches before enabling either prefix.
/// No player result or world reference is cached. Disabled or oversized calls run stock.
/// </summary>
public static class PlayerQueryOptimizations
{
    public const int MaximumCachedClients = 4096;
    public static int MinimumNearbyClients { get; set; } = 128;
    private static volatile bool snapshotEnabled;
    private static volatile bool aroundEnabled;
    [ThreadStatic] private static List<ServerPlayer>? snapshotScratch;
    [ThreadStatic] private static List<IPlayer>? aroundScratch;
    [ThreadStatic] private static bool expectPendingPlayers;
    private static long snapshotCalls, snapshotFallbacks, snapshotFailures, pendingCalls;
    private static long aroundCalls, aroundFallbacks, aroundFailures;

    public static bool SnapshotEnabled { get => snapshotEnabled; set => snapshotEnabled = value; }
    public static bool AroundEnabled { get => aroundEnabled; set => aroundEnabled = value; }

    public static MethodInfo SnapshotTarget => typeof(ServerMain).GetProperty(nameof(ServerMain.AllOnlinePlayers))!.GetMethod!;
    public static MethodInfo AroundTarget => typeof(ServerMain).GetMethod(nameof(ServerMain.GetPlayersAround),
        new[] { typeof(Vec3d), typeof(float), typeof(float), typeof(ActionConsumable<IPlayer>) })!;
    public static MethodInfo SnapshotPrefixMethod => typeof(PlayerQueryOptimizations).GetMethod(nameof(SnapshotPrefix))!;
    public static MethodInfo AroundPrefixMethod => typeof(PlayerQueryOptimizations).GetMethod(nameof(AroundPrefix))!;

    public static bool ValidateContract(out string reason)
    {
        // Parent also verifies the exact official assembly hash and target IL fingerprint.
        bool valid = SnapshotTarget is not null && SnapshotTarget.ReturnType == typeof(IPlayer[]) &&
            AroundTarget is not null && AroundTarget.ReturnType == typeof(IPlayer[]) &&
            typeof(ServerMain).GetField(nameof(ServerMain.Clients))?.FieldType ==
                typeof(Vintagestory.API.Datastructures.CachingConcurrentDictionary<int, ConnectedClient>) &&
            typeof(ConnectedClient).GetField(nameof(ConnectedClient.Player))?.FieldType == typeof(ServerPlayer);
        reason = valid ? "Inspected player-query signature and collection layout match." :
            "Player-query signature or collection layout differs; leave stock methods active.";
        return valid;
    }

    public static PlayerQueryCounters GetCounters() => new(
        Interlocked.Read(ref snapshotCalls), Interlocked.Read(ref snapshotFallbacks),
        Interlocked.Read(ref snapshotFailures), Interlocked.Read(ref pendingCalls),
        Interlocked.Read(ref aroundCalls), Interlocked.Read(ref aroundFallbacks),
        Interlocked.Read(ref aroundFailures));

    /// <summary>Diagnostic/test inspection; both retained lists are empty between calls.</summary>
    public static (int SnapshotCount, int SnapshotCapacity, int AroundCount, int AroundCapacity) CurrentThreadScratch =>
        (snapshotScratch?.Count ?? 0, snapshotScratch?.Capacity ?? 0,
         aroundScratch?.Count ?? 0, aroundScratch?.Capacity ?? 0);

    public static void ClearCurrentThreadScratch()
    {
        snapshotScratch = null;
        aroundScratch = null;
        expectPendingPlayers = false;
    }

    /// <summary>Harmony prefix for ServerMain.get_AllOnlinePlayers. False supplies a fresh result.</summary>
    public static bool SnapshotPrefix(ServerMain __instance, ref IPlayer[] __result)
    {
        if (!snapshotEnabled) return true;
        // Values is the official cached, immutable collection. Do not fetch it twice or
        // iterate the live dictionary, which changes the stock snapshot semantics.
        var clients = __instance.Clients.Values;
        int count = clients.Count;
        if (count > MaximumCachedClients)
        {
            Interlocked.Increment(ref snapshotFallbacks);
            return true;
        }
        Interlocked.Increment(ref snapshotCalls);
        try
        {
            if (!expectPendingPlayers)
            {
                // Most calls have no connecting clients. Preserve stock ServerPlayer[]
                // runtime type as well as fresh caller ownership and enumeration order.
                var result = count == 0 ? Array.Empty<ServerPlayer>() : new ServerPlayer[count];
                int written = 0;
                foreach (var client in clients)
                {
                    var player = client.Player;
                    if (player is not null) result[written++] = player;
                }
                expectPendingPlayers = written != count;
                if (written != count) Array.Resize(ref result, written);
                __result = result;
                return false;
            }

            Interlocked.Increment(ref pendingCalls);
            // Checkout makes recursion use a separate list; a nested finally never
            // overwrites a checked-out outer list. At most one empty list is retained.
            var scratch = snapshotScratch ?? new List<ServerPlayer>(count);
            snapshotScratch = null;
            try
            {
                foreach (var client in clients)
                {
                    var player = client.Player;
                    if (player is not null) scratch.Add(player);
                }
                expectPendingPlayers = scratch.Count != count;
                __result = scratch.ToArray();
                return false;
            }
            finally
            {
                scratch.Clear();
                if (scratch.Capacity <= MaximumCachedClients && snapshotScratch is null)
                    snapshotScratch = scratch;
            }
        }
        catch
        {
            Interlocked.Increment(ref snapshotFailures);
            throw; // Never run stock after partial execution.
        }
    }

    /// <summary>Harmony prefix for the exact official GetPlayersAround overload.</summary>
    public static bool AroundPrefix(ServerMain __instance, Vec3d position, float horRange,
        float vertRange, ActionConsumable<IPlayer>? matches, ref IPlayer[] __result)
    {
        if (!aroundEnabled) return true;
        var clients = __instance.Clients.Values;
        int count = clients.Count;
        if (count > MaximumCachedClients || count < MinimumNearbyClients)
        {
            Interlocked.Increment(ref aroundFallbacks);
            return true;
        }
        Interlocked.Increment(ref aroundCalls);
        var scratch = aroundScratch ?? new List<IPlayer>(count);
        aroundScratch = null;
        try
        {
            float horRangeSq = horRange * horRange;
            foreach (var client in clients)
            {
                // Match official evaluation order, float multiplication, position's
                // dimension behavior and *two* Player reads around a mutating callback.
                if (client.State == EnumClientState.Playing && client.Entityplayer != null &&
                    client.Position.InRangeOf(position, horRangeSq, vertRange) &&
                    (matches == null || matches(client.Player)))
                    scratch.Add(client.Player);
            }
            __result = scratch.ToArray();
            return false;
        }
        catch
        {
            Interlocked.Increment(ref aroundFailures);
            throw; // Callback side effects and exceptions must occur exactly once.
        }
        finally
        {
            scratch.Clear();
            if (scratch.Capacity <= MaximumCachedClients && aroundScratch is null)
                aroundScratch = scratch;
        }
    }
}

public readonly record struct PlayerQueryCounters(long SnapshotCalls, long SnapshotFallbacks,
    long SnapshotFailures, long PendingSnapshotCalls, long AroundCalls, long AroundFallbacks,
    long AroundFailures);
