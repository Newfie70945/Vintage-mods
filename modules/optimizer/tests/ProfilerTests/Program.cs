using System.Globalization;
using System.Text.Json;
using NewfiesOptimizer;

int passed = 0;
void Check(string name, Action run)
{
    run(); passed++; Console.WriteLine("PASS " + name);
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }

Check("tick histogram exact nearest-rank distribution and work/TPS distinction", () =>
{
    var h = new DurationHistogram();
    for (int n = 1; n <= 100; n++) h.Record(n);
    var s = h.Summarize(10);
    Assert(s.Count == 100 && s.AverageMs == 50.5 && s.MedianMs == 50 && s.P95Ms == 95 && s.P99Ms == 99 && s.WorstMs == 100);
    Assert(s.EffectiveTps == 10 && s.MainThreadWorkPercent == 50.5, "TPS must use elapsed wall time, not 1000/averageTick.");
});
Check("empty durations are unavailable, invalid numbers cannot pollute export", () =>
{
    var h = new DurationHistogram(); h.Record(double.NaN); h.Record(double.PositiveInfinity); h.Record(-1);
    var s = h.Summarize(2); Assert(s.Count == 0 && s.AverageMs is null && s.P95Ms is null && s.EffectiveTps is null);
    var hotspot = new HotspotCounter("unused", "test").Snapshot(); Assert(hotspot.AverageMs is null && hotspot.WorstMs is null && hotspot.P95Ms is null);
});
Check("duration overflow is explicit and never disguised as capped percentile", () =>
{
    var h = new DurationHistogram(100); h.Record(1); h.Record(50000); h.Record(double.MaxValue);
    Assert(h.OverflowCount == 2 && h.Percentile(.95) is null && h.WorstMs == double.MaxValue);
});
Check("sub-ms hotspot quantization has precise average and explicit bucket percentile", () =>
{
    var c = new HotspotCounter("phase", "test"); c.Record(.61); c.Calls++;
    var s = c.Snapshot(); Assert(s.AverageMs == .61 && s.P95Ms == .5 && s.WorstMs == .61 && s.Scope.Contains("0.25"));
});
Check("ring retention order and caller-owned snapshots", () =>
{
    var ring = new BoundedRing<int>(3); for (int i = 0; i < 8; i++) ring.Add(i);
    var a = ring.Snapshot(); Assert(a.SequenceEqual(new[] { 5, 6, 7 }) && ring.Overwritten == 5);
    a[0] = -1; Assert(ring.Snapshot()[0] == 5); ring.Add(8); Assert(ring.Snapshot().SequenceEqual(new[] { 6, 7, 8 }));
});
Check("config clamps all memory/work bounds and survives omitted/null sections", () =>
{
    var config = new OptimizerConfig { Profiling = new() { MaxSamples = int.MaxValue, SampleIntervalSeconds = -1, MaxHotspots = 0, SystemSampleEvery = -9, AutoStopSeconds = int.MaxValue }, Benchmarking = new() { MaxEvidenceBytes = int.MaxValue, PresenceIntervalSeconds = 0, RunId = new string('x', 200) }, Logging = null! };
    config.Normalize(); Assert(config.Profiling.MaxSamples == 7200 && config.Profiling.SampleIntervalSeconds == 1 && config.Profiling.MaxHotspots == 8 && config.Profiling.SystemSampleEvery == 1 && config.Profiling.AutoStopSeconds == 86400);
    Assert(config.Benchmarking.MaxEvidenceBytes == 67108864 && config.Benchmarking.PresenceIntervalSeconds == 5 && config.Benchmarking.RunId.Length == 128 && config.Logging.MaxExports == 12);
});
Check("session stop freezes capture and callbacks never retain supplied entities", () =>
{
    var s = new ProfileSession(2, 1); s.Tick(1); s.Tick(2);
    var c = s.FindHotspot("a", "methodA", "test")!; s.Call(c, true, .5); s.Stop();
    s.Tick(500); s.Call(c, true, 500); s.AddSample(new() { ElapsedSeconds = 100 });
    var r = s.Snapshot([], [], "fixture"); Assert(r.Tick.Count == 2 && r.Hotspots.Single().SampledCalls == 1 && r.Samples.Length == 0 && r.GameVersion == "fixture" && !r.IsRunning);
});
Check("hotspot method cap is fixed and omitted lookups are reported", () =>
{
    var s = new ProfileSession(2, 1); Assert(s.FindHotspot("a", "a", "test") is not null);
    Assert(s.FindHotspot("b", "b", "test") is null && s.FindHotspot("c", "c", "test") is null);
    var r = s.Snapshot([], [], "fixture"); Assert(r.Hotspots.Length == 1 && r.HotspotLookupsOmitted == 2);
});
Check("concurrent sampled callbacks preserve count and bounded storage", () =>
{
    var s = new ProfileSession(2, 2); var c = s.FindHotspot("a", "a", "test")!;
    Parallel.For(0, 10000, _ => s.Call(c, true, .25)); var r = s.Snapshot([], [], "fixture");
    Assert(r.Hotspots.Single().Calls == 10000 && r.Hotspots.Single().SampledCalls == 10000 && r.Hotspots.Single().TotalMs == 2500);
});
Check("bounded sample snapshots expose overwritten history and preserve null metrics", () =>
{
    var s = new ProfileSession(2, 2); for (int i = 0; i < 9; i++) s.AddSample(new() { ElapsedSeconds = i });
    var r = s.Snapshot([], [], "fixture"); Assert(r.SamplesOverwritten == 7 && r.Samples.Select(x => x.ElapsedSeconds).SequenceEqual(new double[] { 7, 8 }));
    using var json = JsonDocument.Parse(ReportExporter.ToJson(r));
    Assert(json.RootElement.GetProperty("samples")[0].GetProperty("loadedEntities").ValueKind == JsonValueKind.Null);
    Assert(json.RootElement.GetProperty("schemaVersion").GetInt32() == 1);
});
Check("CSV uses invariant decimal and blanks for unavailable metrics", () =>
{
    var old = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var s = new ProfileSession(2, 2); s.AddSample(new() { ElapsedSeconds = 1.25, ProcessCpuPercent = 23.5 });
        string csv = ReportExporter.ToCsv(s.Snapshot([], [], "fixture"));
        Assert(csv.Contains(",1.25,") && csv.Contains(",23.5,") && !csv.Contains("1,25"));
    }
    finally { CultureInfo.CurrentCulture = old; }
});
Check("atomic export aliases, retention and no leftover temporaries", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "nso-tests-" + Guid.NewGuid());
    try
    {
        var r = new ProfileSession(2, 2).Snapshot([], [], "fixture");
        ReportExporter.Write(path, "profile-1", r, 1); ReportExporter.Write(path, "profile-2", r, 1);
        Assert(File.Exists(Path.Combine(path, "latest.json")) && File.Exists(Path.Combine(path, "latest.csv")));
        Assert(Directory.GetFiles(path, "profile-*.json").Length == 1 && Directory.GetFiles(path, "*.tmp").Length == 0);
        JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "latest.json"))).Dispose();
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
});
Check("opt-in evidence records exact IDs and actual auth state without inventing actions", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "nso-evidence-" + Guid.NewGuid());
    try
    {
        using (var e = new SessionEvidence(path, 65536)) { e.Emit("presence", "fixture-uid", "fixture-run", false); }
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "sessions.jsonl")));
        Assert(doc.RootElement.GetProperty("clientId").GetString() == "fixture-uid" && !doc.RootElement.GetProperty("authenticationEnabled").GetBoolean());
        Assert(!doc.RootElement.TryGetProperty("actionId", out _));
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
});
Check("evidence cap and writer failure are explicit, never unbounded files", () =>
{
    string path = Path.Combine(Path.GetTempPath(), "nso-evidence-cap-" + Guid.NewGuid());
    try
    {
        using (var e = new SessionEvidence(path, 512))
        {
            for (int i = 0; i < 30; i++) e.Emit("presence", new string('u', 200), "fixture", true);
            Thread.Sleep(100); Assert(e.Failure is not null || e.Dropped > 0);
        }
        Assert(new FileInfo(Path.Combine(path, "sessions.jsonl")).Length <= 512);
    }
    finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
});
Check("steady-state histogram insertion allocates zero managed bytes", () =>
{
    var h = new DurationHistogram(); for (int i = 0; i < 10000; i++) h.Record(i % 100);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100000; i++) h.Record(i % 100);
    long delta = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert(delta == 0, "Histogram allocated " + delta + " bytes."); Console.WriteLine("Histogram steady-state allocated bytes: " + delta);
});
Console.WriteLine($"{passed} profiler regression checks passed. Fixtures are not multiplayer or performance capacity benchmarks.");
