using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.API.Datastructures;

string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? throw new ArgumentException("Set VINTAGE_STORY to an official 1.22.7 installation.");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string path in new[] { Path.Combine(game, name.Name + ".dll"), Path.Combine(game, "Lib", name.Name + ".dll"), Path.Combine(game, "Mods", name.Name + ".dll") })
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
    return null;
};
return Entry.Run(args);

static class Entry
{
    private delegate int Read(FastMemoryStream stream, byte[] destination, int offset, int count);
    private sealed record Case(string Name, int BufferSize, int Length, long Position, int DestinationSize, int Offset, int Count, bool Alias = false, bool NullSource = false, bool NullDestination = false, bool Subclass = false);
    private sealed record Outcome(int? Result, string? Error, long Position, byte[]? Buffer, byte[]? Destination);
    private sealed class DerivedStream(byte[] buffer, int length) : FastMemoryStream(buffer, length);
    private static readonly Harmony Harmony = new("newfies.optimizer.tests.stream-copy");
    private static int assertions;
    private static Read Reader() => StreamCopyOptimizations.ReadTarget.CreateDelegate<Read>();
    private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); assertions++; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Run(string[] args)
    {
        Assert(StreamCopyOptimizations.ValidateContract(out string reason), reason);
        Assert(StreamCopyOptimizations.ReadConflictTargets.Length == 3, "Read and both Position accessors must be guarded.");
        if (args.Contains("--benchmark")) { Benchmark(args); return 0; }
        var cases = Cases().ToArray();
        Read read = Reader();
        Outcome[] stock = cases.Select(item => Execute(read, item)).ToArray();
        Patch();
        try
        {
            read = Reader();
            StreamCopyOptimizations.ConfigureRead(false);
            for (int i = 0; i < cases.Length; i++) Compare(stock[i], Execute(read, cases[i]), cases[i].Name + " disabled");
            Assert(StreamCopyOptimizations.Snapshot.AcceleratedCalls == 0, "Disabled prefix must never substitute a copy.");
            StreamCopyOptimizations.ConfigureRead(true);
            for (int i = 0; i < cases.Length; i++) Compare(stock[i], Execute(read, cases[i]), cases[i].Name + " enabled");
            Assert(StreamCopyOptimizations.Snapshot.AcceleratedCalls > 0, "Actual Harmony wrapper must have executed the prefix.");
            var before = StreamCopyOptimizations.Snapshot;
            Parallel.For(0, 8, _ =>
            {
                byte[] source = Enumerable.Range(0, 1024).Select(n => (byte)n).ToArray(), dest = new byte[1024];
                var stream = new FastMemoryStream(source, source.Length);
                for (int i = 0; i < 2000; i++) { stream.Position = 0; if (read(stream, dest, 0, 1024) != 1024 || !dest.AsSpan().SequenceEqual(source)) throw new Exception("Concurrent separate-stream copy corruption."); }
            });
            var after = StreamCopyOptimizations.Snapshot;
            Assert(after.AcceleratedCalls - before.AcceleratedCalls == 16000 && after.CopiedBytes - before.CopiedBytes == 16000L * 1024, "Atomic counters must include all independent-stream workers.");
            var fixture = new FastMemoryStream(new byte[4096], 4096); byte[] destination = new byte[4096];
            for (int i = 0; i < 5000; i++) { fixture.Position = 0; read(fixture, destination, 0, 4096); }
            long minimumAllocation = long.MaxValue;
            // Tiered JIT/runtime bookkeeping can appear in the first measured batch.
            // A genuinely allocating copy loop cannot reach zero in any 20,000-call batch.
            for (int batch = 0; batch < 5; batch++)
            {
                long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20000; i++) { fixture.Position = 0; read(fixture, destination, 0, 4096); }
                minimumAllocation = Math.Min(minimumAllocation, GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
            }
            Assert(minimumAllocation == 0, "Eligible copy path must allocate zero managed bytes after warmup; best batch allocated " + minimumAllocation);
            Assert(after.Failures == 0, "Validated official copy path must not report a failure.");
            StreamCopyOptimizations.ConfigureRead(false);
        }
        finally { Harmony.Unpatch(StreamCopyOptimizations.ReadTarget, HarmonyPatchType.All, Harmony.Id); }
        Console.WriteLine($"PASS {cases.Length} official-engine differential cases in each enabled/disabled state; {assertions} assertions, independent-stream concurrency, zero steady-state allocation. No server throughput claim.");
        return 0;
    }

    private static void Patch() => Harmony.Patch(StreamCopyOptimizations.ReadTarget, prefix: new HarmonyMethod(StreamCopyOptimizations.ReadPrefixMethod));
    private static Outcome Execute(Read read, Case item)
    {
        byte[]? source = item.NullSource ? null : Enumerable.Range(0, item.BufferSize).Select(n => (byte)(n * 37 + 17)).ToArray();
        byte[]? destination = item.Alias ? source : item.NullDestination ? null : Enumerable.Repeat((byte)0xc7, item.DestinationSize).ToArray();
        FastMemoryStream stream = item.Subclass ? new DerivedStream(source!, item.Length) : new FastMemoryStream(source!, item.Length);
        stream.Position = item.Position;
        int? result = null; string? error = null;
        try { result = read(stream, destination!, item.Offset, item.Count); }
        catch (Exception exception) { error = exception.GetType().FullName + ": " + exception.Message; }
        return new(result, error, stream.Position, source, destination);
    }
    private static void Compare(Outcome expected, Outcome actual, string name)
    {
        Assert(expected.Result == actual.Result, name + " result");
        Assert(expected.Error == actual.Error, name + " exception type/message");
        Assert(expected.Position == actual.Position, name + " position after return/exception");
        Assert(expected.Buffer is null ? actual.Buffer is null : actual.Buffer is not null && expected.Buffer.AsSpan().SequenceEqual(actual.Buffer), name + " backing contents");
        Assert(expected.Destination is null ? actual.Destination is null : actual.Destination is not null && expected.Destination.AsSpan().SequenceEqual(actual.Destination), name + " destination including partial writes");
    }
    private static IEnumerable<Case> Cases()
    {
        int[] counts = [0, 1, 2, 7, 16, 64, 127, 128, 192, 255, 256, 257, 511, 512, 1024, 4096, 65536, 1048576];
        foreach (int count in counts) foreach (int alignment in new[] { 0, 1, 3, 7, 13, 31 })
        {
            yield return new($"valid {count}/{alignment}", count + 64, count + 64, alignment, count + 64, 31 - alignment, count);
            yield return new($"short {count}/{alignment}", count + 64, count + 64, count + 32, count + 64, alignment, count);
        }
        foreach (int position in new[] { 0, 1, 7, 64, 128, 255, 256 }) foreach (int offset in new[] { 0, 1, 7, 64, 128, 255, 256 }) foreach (int count in new[] { 0, 1, 7, 255, 256, 512 })
            yield return new($"alias {position}/{offset}/{count}", 1024, 1024, position, 1024, offset, count, Alias: true);
        foreach (long position in new[] { long.MinValue, -1L, 0L, 1L, 511L, 512L, 513L, int.MaxValue + 1L, long.MaxValue })
        foreach (int offset in new[] { int.MinValue, -1, 0, 1, 255, 512, 513, int.MaxValue })
        foreach (int count in new[] { int.MinValue, -1, 0, 1, 255, 256, 512, int.MaxValue })
            yield return new($"invalid {position}/{offset}/{count}", 512, 512, position, 512, offset, count);
        foreach (int length in new[] { -1, 0, 256, 512, 513, 1024 }) foreach (long position in new[] { -1L, 0L, 1L, 256L, 512L })
        foreach (int count in new[] { -1, 0, 1, 256, 512, 1024 })
            yield return new($"layout {length}/{position}/{count}", 512, length, position, 1024, 0, count);
        foreach (bool nullSource in new[] { false, true }) foreach (bool nullDest in new[] { false, true })
        foreach (int count in new[] { -1, 0, 1, 256, 512 }) foreach (long position in new[] { -1L, 0L, 256L, 512L })
            yield return new($"null {nullSource}/{nullDest}/{count}/{position}", 512, 512, position, 512, 0, count, NullSource: nullSource, NullDestination: nullDest);
        yield return new("derived stream", 512, 512, 0, 512, 0, 512, Subclass: true);
        var random = new Random(718023);
        for (int i = 0; i < 4000; i++)
        {
            int size = random.Next(0, 2048), dest = random.Next(0, 2048);
            yield return new("random " + i, size, random.Next(-1, size + 2), random.Next(-1, size + 2), dest, random.Next(-1, dest + 2), random.Next(-1, 2049), Alias: i % 9 == 0);
        }
    }

    private sealed record Measurement(int Round, string Mode, int Count, int Alignment, int Iterations, double NanosecondsPerCall, double AllocatedBytesPerCall, long AcceleratedCalls);
    private static void Benchmark(string[] args)
    {
        string output = args.SkipWhile(s => s != "--output").Skip(1).FirstOrDefault() ?? "stream-copy-results.json";
        int[] sizes = [0, 1, 2, 8, 16, 32, 64, 96, 127, 128, 192, 255, 256, 512, 1024, 4096, 16384, 65536, 1048576];
        var results = new List<Measurement>();
        for (int round = 0; round < 5; round++)
        foreach (string mode in round % 2 == 0 ? new[] { "stock", "prefix-disabled", "bulk-enabled" } : new[] { "bulk-enabled", "prefix-disabled", "stock" })
        {
            if (mode != "stock") Patch();
            StreamCopyOptimizations.ConfigureRead(mode == "bulk-enabled");
            Read read = Reader();
            try
            {
                foreach (int size in sizes) foreach (int alignment in new[] { 0, 1, 7, 31 })
                {
                    int iterations = (int)Math.Clamp(16L * 1024 * 1024 / Math.Max(1, size), 32, 100000);
                    byte[] source = new byte[size + 64], destination = new byte[size + 64]; new Random(17).NextBytes(source);
                    var stream = new FastMemoryStream(source, source.Length);
                    for (int i = 0; i < Math.Clamp(2 * 1024 * 1024 / Math.Max(1, size), 2, 5000); i++) { stream.Position = alignment; read(stream, destination, 31 - alignment, size); }
                    long accelerated = StreamCopyOptimizations.Snapshot.AcceleratedCalls;
                    long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                    int sum = 0;
                    for (int i = 0; i < iterations; i++) { stream.Position = alignment; sum += read(stream, destination, 31 - alignment, size); }
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    long allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    GC.KeepAlive(sum); GC.KeepAlive(destination);
                    long copied = StreamCopyOptimizations.Snapshot.AcceleratedCalls - accelerated;
                    if (mode == "bulk-enabled" && size >= StreamCopyOptimizations.MinimumReadBytes && copied != iterations) throw new Exception("Benchmark did not execute actual Harmony copy candidate.");
                    if (mode != "bulk-enabled" && copied != 0) throw new Exception("Stock/disabled benchmark unexpectedly substituted a copy.");
                    results.Add(new(round, mode, size, alignment, iterations, elapsed * (1e9 / Stopwatch.Frequency) / iterations, (double)allocation / iterations, copied));
                }
            }
            finally { if (mode != "stock") Harmony.Unpatch(StreamCopyOptimizations.ReadTarget, HarmonyPatchType.All, Harmony.Id); }
            Console.WriteLine($"Measured round {round + 1}/5: {mode}");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount, threshold = StreamCopyOptimizations.MinimumReadBytes, measurements = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Wrote " + output);
        foreach (int size in sizes)
        {
            double Median(string mode) { double[] values = results.Where(m => m.Mode == mode && m.Count == size).Select(m => m.NanosecondsPerCall).Order().ToArray(); return values[values.Length / 2]; }
            double stock = Median("stock"), disabled = Median("prefix-disabled"), enabled = Median("bulk-enabled");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{size,8} bytes: stock {stock,10:F2} ns; disabled {disabled,10:F2}; enabled {enabled,10:F2}; ratio {enabled / stock:F3}"));
        }
    }
}
