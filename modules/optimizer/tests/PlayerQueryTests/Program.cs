using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;

string engine = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new ArgumentException("Set VINTAGE_STORY to the exact official 1.22.7 installation.");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string path in new[] { Path.Combine(engine, name.Name + ".dll"), Path.Combine(engine, "Lib", name.Name + ".dll"), Path.Combine(engine, "Mods", name.Name + ".dll") })
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
    return null;
};
Queries.Run(args);

static class Queries
{
    private delegate IPlayer[] AroundQuery(ServerMain server, Vec3d position, float horizontal, float vertical, ActionConsumable<IPlayer>? predicate);
    private static readonly System.Func<ServerMain, IPlayer[]> Snapshot = PlayerQueryOptimizations.SnapshotTarget.CreateDelegate<System.Func<ServerMain, IPlayer[]>>();
    private static readonly AroundQuery Around = PlayerQueryOptimizations.AroundTarget.CreateDelegate<AroundQuery>();
    private static readonly Harmony Patcher = new("newfies.optimizer.player-query.tests");
    private static int checks, sink;
    private static readonly Vec3d Origin = new(0, 0, 0);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--benchmark")
        {
            Benchmark(args.Length > 1 ? args[1] : "benchmarks/player-queries/results.json");
            return;
        }
        PlayerQueryOptimizations.MinimumNearbyClients = 0;
        Check(PlayerQueryOptimizations.ValidateContract(out var reason), reason);
        var fixture = Fixture.Create(10, 2);
        var stockSnapshot = Snapshot(fixture.Server);
        var stockAround = Around(fixture.Server, Origin, 100, 100, null);
        Install();
        Enable();
        Check(Same(stockSnapshot, Snapshot(fixture.Server)), "snapshot exact official ordering and pending filter");
        Check(Snapshot(fixture.Server).GetType() == typeof(ServerPlayer[]), "snapshot concrete runtime array type");
        Check(Same(stockAround, Around(fixture.Server, Origin, 100, 100, null)), "nearby exact official ordering");
        var first = Snapshot(fixture.Server);
        first[0] = null!;
        Check(Snapshot(fixture.Server)[0] is not null, "snapshot caller array ownership");
        var firstAround = Around(fixture.Server, Origin, 100, 100, null);
        firstAround[0] = null!;
        Check(Around(fixture.Server, Origin, 100, 100, null)[0] is not null, "nearby caller array ownership");

        // Use independent fixtures for callback side effects; compare actual official bodies
        // (unpatched) with the same methods after Harmony injection.
        Patcher.UnpatchAll(Patcher.Id);
        var geometry = Fixture.Create(10, 2);
        geometry.Clients[1].WorldData.EntityPlayer.Pos.Dimension = 1;
        geometry.Clients[2].State = EnumClientState.Connecting;
        geometry.Clients[3].WorldData.EntityPlayer = null!;
        geometry.Clients[4].WorldData.EntityPlayer.Pos.SetPos(3, 4, 0);
        var ranges = new (Vec3d Position, float H, float V)[]
        {
            (Origin, 0, 0), (Origin, 3, 4), (Origin, -3, 4), (Origin, 10, -1),
            (new Vec3d(0, 32768, 0), 100, 10), (Origin, float.NaN, 10),
            (Origin, float.PositiveInfinity, float.PositiveInfinity), (Origin, 100, float.NaN)
        };
        var expected = ranges.Select(r => Around(geometry.Server, r.Position, r.H, r.V, null)).ToArray();
        int stockPredicates = 0;
        var stockFiltered = Around(geometry.Server, Origin, 100, 100, p => { stockPredicates++; return !ReferenceEquals(p, geometry.Clients[0].Player); });
        Install(); Enable();
        for (int i = 0; i < ranges.Length; i++)
            Check(Same(expected[i], Around(geometry.Server, ranges[i].Position, ranges[i].H, ranges[i].V, null)), $"range/dimension/float case {i}");
        int optimizedPredicates = 0;
        var optimizedFiltered = Around(geometry.Server, Origin, 100, 100, p => { optimizedPredicates++; return !ReferenceEquals(p, geometry.Clients[0].Player); });
        Check(Same(stockFiltered, optimizedFiltered) && stockPredicates == optimizedPredicates, "predicate invocation count/order and short circuit");

        var mutation = Fixture.Create(4);
        var replacement = New<ServerPlayer>();
        var victim = mutation.Clients[0];
        var old = victim.Player;
        var mutated = Around(mutation.Server, Origin, 100, 100, p => { if (ReferenceEquals(p, old)) victim.Player = replacement; return true; });
        Check(mutated.Contains(replacement) && !mutated.Contains(old), "predicate mutation preserves official second Player read");
        victim.Player = old;
        var extra = Fixture.Create(1).Clients[0];
        int mutationCalls = 0;
        var mutatedDictionary = Around(mutation.Server, Origin, 100, 100, _ =>
        {
            if (mutationCalls++ == 0) mutation.Server.Clients.TryAdd(1000, extra);
            return true;
        });
        Check(mutationCalls == 4 && mutatedDictionary.Length == 4, "captured immutable client snapshot during dictionary mutation");
        Check(Around(mutation.Server, Origin, 100, 100, null).Length == 5, "next query sees newly added client");

        var recursion = Fixture.Create(5);
        int recursionCalls = 0;
        var recursive = Around(recursion.Server, Origin, 100, 100, _ =>
        {
            recursionCalls++;
            var inner = Around(recursion.Server, Origin, 2, 2, null);
            if (inner.Length > 0) inner[0] = null!;
            return true;
        });
        Check(recursive.Length == 5 && recursive.All(p => p is not null) && recursionCalls == 5, "reentrant queries own separate scratch and result arrays");

        int throwCalls = 0;
        var thrown = new InvalidOperationException("intentional callback failure");
        Exception? seen = null;
        try { Around(recursion.Server, Origin, 100, 100, _ => { if (++throwCalls == 2) throw thrown; return true; }); }
        catch (Exception error) { seen = error; }
        Check(ReferenceEquals(seen, thrown) && throwCalls == 2, "exception propagated once with no stock replay");
        Check(PlayerQueryOptimizations.CurrentThreadScratch.AroundCount == 0, "scratch cleared after partial callback failure");
        Check(Around(recursion.Server, Origin, 100, 100, null).Length == 5, "query remains correct after callback failure");

        var disabledInFlight = Fixture.Create(5);
        int disableCalls = 0;
        var complete = Around(disabledInFlight.Server, Origin, 100, 100, _ => { if (++disableCalls == 1) PlayerQueryOptimizations.AroundEnabled = false; return true; });
        Check(complete.Length == 5 && disableCalls == 5, "disable during callback completes one bounded invocation without replay");
        Check(Around(disabledInFlight.Server, Origin, 100, 100, null).Length == 5, "next disabled call uses official method");
        Enable();

        var oversized = Fixture.Create(PlayerQueryOptimizations.MaximumCachedClients + 1);
        var before = PlayerQueryOptimizations.GetCounters();
        Check(Snapshot(oversized.Server).Length == PlayerQueryOptimizations.MaximumCachedClients + 1 && Around(oversized.Server, Origin, 1000, 1000, null).Length == PlayerQueryOptimizations.MaximumCachedClients + 1, "oversized official fallback correctness");
        var after = PlayerQueryOptimizations.GetCounters();
        Check(after.SnapshotFallbacks == before.SnapshotFallbacks + 1 && after.AroundFallbacks == before.AroundFallbacks + 1, "oversized fallback diagnostics");
        Check(PlayerQueryOptimizations.CurrentThreadScratch.SnapshotCapacity <= PlayerQueryOptimizations.MaximumCachedClients && PlayerQueryOptimizations.CurrentThreadScratch.AroundCapacity <= PlayerQueryOptimizations.MaximumCachedClients, "scratch capacity bounded");
        var pending = Fixture.Create(64, 8);
        Snapshot(pending.Server); Snapshot(pending.Server);
        foreach (var client in pending.Clients.Where(c => c.Player is null)) client.Player = New<ServerPlayer>();
        Check(Snapshot(pending.Server).Length == 72 && Snapshot(pending.Server).Length == 72, "adaptive pending-to-playing transition");
        Check(PlayerQueryOptimizations.CurrentThreadScratch.SnapshotCount == 0 && PlayerQueryOptimizations.CurrentThreadScratch.AroundCount == 0, "all retained lists empty after query");

        Parallel.For(0, 8, worker =>
        {
            var local = Fixture.Create(20 + worker, worker % 3);
            for (int i = 0; i < 500; i++)
            {
                if (Snapshot(local.Server).Length != 20 + worker || Around(local.Server, Origin, 100, 100, null).Length != 20 + worker)
                    throw new Exception("Thread-local scratch corruption");
            }
            var scratch = PlayerQueryOptimizations.CurrentThreadScratch;
            if (scratch.SnapshotCount != 0 || scratch.AroundCount != 0) throw new Exception("Thread retains players");
        });
        Check(true, "concurrent worker threads use isolated empty scratch");
        PlayerQueryOptimizations.SnapshotEnabled = false;
        PlayerQueryOptimizations.AroundEnabled = false;
        Check(Same(stockSnapshot, Snapshot(fixture.Server)) && Same(stockAround, Around(fixture.Server, Origin, 100, 100, null)), "both independent disabled prefixes preserve stock");
        Patcher.UnpatchAll(Patcher.Id);
        Console.WriteLine($"{checks} player-query checks passed against actual official methods and injected prefixes. Fixtures are uninitialized engine objects, not bots, players or a running server.");
    }

    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; Console.WriteLine("PASS " + reason); }
    private static bool Same(IPlayer[] left, IPlayer[] right) => left.Length == right.Length && left.Zip(right).All(pair => ReferenceEquals(pair.First, pair.Second));
    private static T New<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Enable() { PlayerQueryOptimizations.SnapshotEnabled = true; PlayerQueryOptimizations.AroundEnabled = true; }
    private static void Install()
    {
        Patcher.Patch(PlayerQueryOptimizations.SnapshotTarget, new HarmonyMethod(PlayerQueryOptimizations.SnapshotPrefixMethod));
        Patcher.Patch(PlayerQueryOptimizations.AroundTarget, new HarmonyMethod(PlayerQueryOptimizations.AroundPrefixMethod));
    }

    private sealed record Fixture(ServerMain Server, ConnectedClient[] Clients)
    {
        public static Fixture Create(int playing, int pending = 0)
        {
            var server = New<ServerMain>();
            server.Clients = new CachingConcurrentDictionary<int, ConnectedClient>();
            var clients = new ConnectedClient[playing + pending];
            for (int i = 0; i < clients.Length; i++)
            {
                var client = New<ConnectedClient>();
                if (i < playing)
                {
                    var entity = New<EntityPlayer>();
                    typeof(Entity).GetProperty(nameof(Entity.Pos))!.SetValue(entity, new EntityPos(i % 20, i % 10, i % 20));
                    var data = New<ServerWorldPlayerData>(); data.EntityPlayer = entity;
                    client.WorldData = data; client.Player = New<ServerPlayer>(); client.State = EnumClientState.Playing;
                }
                else client.State = EnumClientState.Connecting;
                server.Clients.TryAdd(i, client); clients[i] = client;
            }
            _ = server.Clients.Values; // Materialize the official immutable collection outside measurement.
            return new(server, clients);
        }
    }

    private static void Benchmark(string output)
    {
        const int iterations = 10000, repeats = 5, warmup = 1000;
        var records = new List<object>();
        var random = new Random(1227);
        foreach (int count in new[] { 1, 64, 200, 2000 })
        foreach (bool connecting in new[] { false, true })
        foreach (string query in new[] { "online-snapshot", "nearby-all", "nearby-half" })
        {
            var fixture = Fixture.Create(count, connecting ? Math.Max(1, count / 8) : 0);
            ActionConsumable<IPlayer> half = p => Array.IndexOf(fixture.Clients, fixture.Clients.First(c => ReferenceEquals(c.Player, p))) % 2 == 0;
            // The selective callback is intentionally constant-time so it does not hide
            // collection allocation or method overhead behind fixture lookup work.
            var selected = new HashSet<IPlayer>(fixture.Clients.Take(count).Where((_, i) => i % 2 == 0).Select(c => (IPlayer)c.Player));
            half = selected.Contains;
            System.Func<IPlayer[]> invoke = query == "online-snapshot" ? () => Snapshot(fixture.Server) :
                () => Around(fixture.Server, Origin, 1000, 1000, query == "nearby-half" ? half : null);
            for (int repeat = 0; repeat < repeats; repeat++)
            foreach (string variant in new[] { "stock", "prefix-disabled", "optimized" }.OrderBy(_ => random.Next()))
            {
                Patcher.UnpatchAll(Patcher.Id);
                PlayerQueryOptimizations.ClearCurrentThreadScratch();
                PlayerQueryOptimizations.SnapshotEnabled = false; PlayerQueryOptimizations.AroundEnabled = false;
                if (variant != "stock" && !(variant == "optimized" && query.StartsWith("nearby") && fixture.Clients.Length < PlayerQueryOptimizations.MinimumNearbyClients)) Install();
                if (variant == "optimized") Enable();
                for (int i = 0; i < warmup; i++) sink = invoke().Length;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long startBytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                int lengths = 0;
                for (int i = 0; i < iterations; i++) lengths += invoke().Length;
                long elapsed = Stopwatch.GetTimestamp() - start;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
                Volatile.Write(ref sink, lengths);
                records.Add(new { query, playing = count, pending = fixture.Clients.Length - count, variant, repeat,
                    iterations, nanosecondsPerCall = elapsed * (1e9 / Stopwatch.Frequency) / iterations, bytesPerCall = (double)bytes / iterations });
            }
        }
        Patcher.UnpatchAll(Patcher.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            scope = "Actual official 1.22.7 query methods, uninitialized fixtures, Harmony prefixes; component microbenchmark only. No authenticated clients, network workload, tick-time or whole-server claim.",
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            runtime = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            engineAssembly = typeof(ServerMain).Assembly.Location,
            iterations, repeats, warmup, seed = 1227, records
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote {records.Count} randomized component measurements to {output}");
    }
}
