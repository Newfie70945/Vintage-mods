using System;
using System.Diagnostics;

namespace BlockLogger.Services;

/// <summary>
/// Sums many small callback timings into one SuiteTimingRegistry record per window.
/// A "slow >= 1 ms" threshold hides a mass load or unload where thousands of individually fast
/// callbacks (a despawn handler, a column-load hook) add up to a visible stall. This reports the
/// total time per active window instead, so the registry's P95 and maximum show the worst bursts.
/// </summary>
public sealed class SuiteBurstTimer
{
    private readonly object gate = new();
    private readonly string metric;
    private readonly long windowTicks;
    private bool started;
    private long windowStart, totalTicks;

    public SuiteBurstTimer(string metric, double windowSeconds = 1.0)
    {
        this.metric = metric;
        windowTicks = (long)(Math.Clamp(windowSeconds, 0.05, 60) * Stopwatch.Frequency);
    }

    /// <summary>Adds one finished call, measured in Stopwatch ticks.</summary>
    public void Add(long elapsedTicks) => Add(elapsedTicks, Stopwatch.GetTimestamp());

    /// <summary>Same as <see cref="Add(long)"/> with an explicit clock reading.</summary>
    public void Add(long elapsedTicks, long nowTimestamp)
    {
        if (elapsedTicks < 0) elapsedTicks = 0;
        double flushMilliseconds = -1;
        lock (gate)
        {
            if (!started) { started = true; windowStart = nowTimestamp; }
            totalTicks += elapsedTicks;
            if (nowTimestamp - windowStart >= windowTicks)
            {
                flushMilliseconds = ToMilliseconds(totalTicks);
                totalTicks = 0;
                windowStart = nowTimestamp;
            }
        }
        // Recorded outside the gate: the registry takes its own lock.
        if (flushMilliseconds >= 0) SuiteTimingRegistry.Record(metric, flushMilliseconds);
    }

    /// <summary>Records any partial window, for example when the owner is disposed.</summary>
    public void Flush()
    {
        double milliseconds;
        lock (gate)
        {
            milliseconds = ToMilliseconds(totalTicks);
            totalTicks = 0;
            started = false;
        }
        if (milliseconds > 0) SuiteTimingRegistry.Record(metric, milliseconds);
    }

    private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
