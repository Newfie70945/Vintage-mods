using System;
using System.Diagnostics;
using Vintagestory.API.Server;

namespace BlockLogger.AntiCheat;

internal readonly struct PacketGuardTpsSnapshot
{
    public PacketGuardTpsSnapshot(
        bool enabled,
        bool ready,
        double targetTps,
        double estimatedTps,
        double healthPercent,
        double timingMultiplier,
        string band,
        int sampleCount,
        double smoothedTickMs,
        bool suppressTimingRejections)
    {
        Enabled = enabled;
        Ready = ready;
        TargetTps = targetTps;
        EstimatedTps = estimatedTps;
        HealthPercent = healthPercent;
        TimingMultiplier = timingMultiplier;
        Band = band;
        SampleCount = sampleCount;
        SmoothedTickMs = smoothedTickMs;
        SuppressTimingRejections = suppressTimingRejections;
    }

    public bool Enabled { get; }
    public bool Ready { get; }
    public double TargetTps { get; }
    public double EstimatedTps { get; }
    public double HealthPercent { get; }
    public double TimingMultiplier { get; }
    public string Band { get; }
    public int SampleCount { get; }
    public double SmoothedTickMs { get; }
    public bool SuppressTimingRejections { get; }
    public bool IsCritical => string.Equals(Band, "Critical", StringComparison.OrdinalIgnoreCase);
    public bool IsDegraded => IsCritical || string.Equals(Band, "Degraded", StringComparison.OrdinalIgnoreCase);

    public string Evidence =>
        $"tpsBand={Band};targetTps={TargetTps:0.0};estimatedTps={EstimatedTps:0.0};tpsHealthPct={HealthPercent:0.0};" +
        $"timingMultiplier={TimingMultiplier:0.00};tpsSamples={SampleCount};tpsReady={Ready};timingRejectSuppressed={SuppressTimingRejections}";
}

/// <summary>
/// Measures actual server main-thread tick cadence against the configured server TickTime.
/// The sampler asks to run more frequently than one configured tick; because Vintage Story
/// processes game-tick listeners on fixed game ticks, this results in one lightweight sample
/// per real server tick without relying on client ping or game-world time.
///
/// Degradation reacts quickly and recovery deliberately reacts slowly. That asymmetry prevents
/// a single healthy tick immediately re-arming timing-sensitive rejection after a lag burst.
/// </summary>
internal sealed class ServerTickHealthMonitor : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly object sync = new();

    private long listenerId;
    private long lastStamp;
    private double targetTickMs = 50.0;
    private double targetTps = 20.0;
    private double smoothedTickMs = 50.0;
    private int sampleCount;
    private bool started;

    public ServerTickHealthMonitor(ICoreServerAPI api, AntiCheatConfig config)
    {
        this.api = api;
        this.config = config;
    }

    public void Start()
    {
        if (started) return;
        started = true;

        try
        {
            double configured = api.Server.Config.TickTime;
            if (double.IsFinite(configured) && configured >= 5.0 && configured <= 1000.0)
                targetTickMs = configured;
        }
        catch
        {
            targetTickMs = 50.0;
        }

        targetTps = 1000.0 / Math.Max(1.0, targetTickMs);
        smoothedTickMs = targetTickMs;
        lastStamp = 0;
        sampleCount = 0;

        // Request less than one configured tick so the listener is eligible every actual tick.
        // The engine still invokes it only from fixed game ticks.
        int sampleIntervalMs = Math.Clamp((int)Math.Round(targetTickMs / 2.0), 5, 250);
        listenerId = api.Event.RegisterGameTickListener(OnTick, sampleIntervalMs, sampleIntervalMs);
    }

    private void OnTick(float dt)
    {
        long stamp = Stopwatch.GetTimestamp();
        lock (sync)
        {
            if (lastStamp == 0)
            {
                lastStamp = stamp;
                return;
            }

            double elapsedMs = (stamp - lastStamp) * 1000.0 / Stopwatch.Frequency;
            lastStamp = stamp;
            if (!double.IsFinite(elapsedMs) || elapsedMs <= 0) return;

            // If the scheduler ever catches up by invoking this listener twice inside one fixed
            // server tick, ignore that sub-tick callback rather than averaging it as a healthy
            // tick. lastStamp still advances, so the next real tick is measured normally.
            if (elapsedMs < targetTickMs * 0.50) return;

            // Keep long stalls bounded so one debugger pause or save spike cannot leave
            // PacketGuard permanently relaxed.
            double upperBound = targetTickMs * Math.Max(4.0, config.PacketGuardTpsMaxTimingMultiplier * 2.0);
            double observedTickMs = Math.Clamp(elapsedMs, targetTickMs, upperBound);

            // Worsen quickly, recover slowly. This is intentionally conservative because the
            // purpose of TPS awareness is false-positive resistance, not a precise profiler chart.
            double alpha = observedTickMs > smoothedTickMs ? 0.30 : 0.045;
            smoothedTickMs += (observedTickMs - smoothedTickMs) * alpha;
            smoothedTickMs = Math.Clamp(smoothedTickMs, targetTickMs, upperBound);
            sampleCount = Math.Min(sampleCount + 1, 1_000_000);
        }
    }

    public PacketGuardTpsSnapshot GetSnapshot()
    {
        lock (sync)
        {
            double estimated = 1000.0 / Math.Max(1.0, smoothedTickMs);
            double health = Math.Clamp(estimated / Math.Max(0.1, targetTps) * 100.0, 0.0, 100.0);
            int warmupSamples = Math.Clamp((int)Math.Ceiling(targetTps * 2.0), 12, 80);
            bool ready = sampleCount >= warmupSamples;

            string band;
            if (!config.PacketGuardTpsAwarenessEnabled) band = "Disabled";
            else if (!ready) band = "Warmup";
            else if (health < config.PacketGuardTpsCriticalPercent) band = "Critical";
            else if (health < config.PacketGuardTpsDegradedPercent) band = "Degraded";
            else band = "Healthy";

            double multiplier = 1.0;
            if (config.PacketGuardTpsAwarenessEnabled && health < config.PacketGuardTpsDegradedPercent)
            {
                multiplier = Math.Clamp(targetTps / Math.Max(0.1, estimated), 1.0, config.PacketGuardTpsMaxTimingMultiplier);
            }

            bool suppress = config.PacketGuardTpsAwarenessEnabled &&
                            ready &&
                            config.PacketGuardTpsSuppressTimingRejectsWhenCritical &&
                            health < config.PacketGuardTpsCriticalPercent;

            return new PacketGuardTpsSnapshot(
                config.PacketGuardTpsAwarenessEnabled,
                ready,
                targetTps,
                estimated,
                health,
                multiplier,
                band,
                sampleCount,
                smoothedTickMs,
                suppress);
        }
    }

    public void Dispose()
    {
        if (!started) return;
        started = false;
        if (listenerId != 0)
        {
            try { api.Event.UnregisterGameTickListener(listenerId); } catch { }
            listenerId = 0;
        }
    }
}
