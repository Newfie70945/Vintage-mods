using System.Diagnostics;
using System.Reflection;

namespace NewfiesOptimizer;

public sealed class ProfileSession
{
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DurationHistogram ticks = new();
    private readonly BoundedRing<TelemetrySample> samples;
    private readonly Dictionary<object, HotspotCounter> hotspots = new();
    private readonly int maxHotspots;
    private long omitted;
    private double stoppedSeconds;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public bool Running { get; private set; } = true;
    public double ElapsedSeconds => Running ? clock.Elapsed.TotalSeconds : stoppedSeconds;
    public long HookCalls, SampleCalls;
    public double HookWorkMs, SampleWorkMs;
    public long TickCount { get { lock (gate) return ticks.Count; } }

    public ProfileSession(int maxSamples, int maxHotspots)
    {
        samples = new(maxSamples); this.maxHotspots = maxHotspots;
    }
    public void Tick(double ms) { lock (gate) if (Running) ticks.Record(ms); }
    public void AddSample(TelemetrySample value) { lock (gate) if (Running) samples.Add(value); }
    public void Stop() { lock (gate) { if (!Running) return; stoppedSeconds = clock.Elapsed.TotalSeconds; Running = false; clock.Stop(); } }
    public void AddHookOverhead(double ms) { lock (gate) { HookCalls++; HookWorkMs += ms; } }
    public void AddSampleOverhead(double ms) { lock (gate) { SampleCalls++; SampleWorkMs += ms; } }
    public HotspotCounter? FindHotspot(object key, string name, string category)
    {
        lock (gate)
        {
            if (hotspots.TryGetValue(key, out var value)) return value;
            if (hotspots.Count >= maxHotspots) { omitted++; return null; }
            value = new(name, category); hotspots.Add(key, value); return value;
        }
    }
    public bool TryExistingHotspot(object key, out HotspotCounter? counter)
    {
        lock (gate) return hotspots.TryGetValue(key, out counter);
    }
    public bool HasHotspotCapacity { get { lock (gate) return hotspots.Count < maxHotspots; } }
    public void OmitHotspotLookup() { lock (gate) omitted++; }
    public void Call(HotspotCounter counter, bool sampled, double ms)
    {
        lock (gate)
        {
            if (!Running) return;
            counter.Calls++;
            if (sampled) counter.Record(ms);
        }
    }
    public TickSummary GetTickSummary() { lock (gate) return ticks.Summarize(ElapsedSeconds); }
    public ProfileReport Snapshot(MetricCapability[] capabilities, ModuleStatus[] modules, string gameVersion)
    {
        lock (gate)
        {
            double elapsed = ElapsedSeconds;
            return new()
            {
                StartedUtc = StartedUtc, StoppedUtc = StartedUtc.AddSeconds(elapsed), DurationSeconds = elapsed,
                IsRunning = Running, GameVersion = gameVersion, Tick = ticks.Summarize(elapsed), Samples = samples.Snapshot(),
                Hotspots = hotspots.Values.Select(x => x.Snapshot()).OrderByDescending(x => x.TotalMs).ToArray(),
                Capabilities = capabilities, Modules = modules, SamplesOverwritten = samples.Overwritten,
                HotspotLookupsOmitted = omitted, Overhead = new(HookCalls, HookWorkMs, SampleCalls, SampleWorkMs),
                Notes = ["Engine-patch preview. Module activation is recorded separately; AI tick cadence is unchanged. Optional main-thread path assistance changes path scheduling only.",
                    "Process CPU is percent of one logical core and may exceed 100%. Main-thread work utilization is wall-time processing, not CPU utilization.",
                    "System/phase/callback timings are opt-in inclusive samples. Totals cover sampled calls only and are not extrapolated.",
                    "GC committed and fragmentation describe the most recent GC; managedHeapBytes is the current approximate live allocation estimate, not a retained-object census.",
                    "Network counters are engine-reported packet payload bytes, not physical-interface throughput. No per-player attribution is claimed.",
                    "Historical samples are bounded; overwritten count is explicit. Tick histogram covers the full capture."]
            };
        }
    }
}
