using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.Services;

/// <summary>
/// Tiny in-memory timing registry for suite-owned work. Recording is intentionally bounded
/// and does not log per-operation data; the dashboard reads aggregate rolling statistics.
/// </summary>
public static class SuiteTimingRegistry
{
    private const int WindowSize = 128;
    private const int RateWindowSeconds = 60;
    private const int MaxRateBuckets = 70;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, MetricState> Metrics = new(StringComparer.Ordinal);

    public static void Record(string name, double milliseconds)
    {
        if (string.IsNullOrWhiteSpace(name) || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return;
        milliseconds = Math.Max(0, milliseconds);

        lock (Gate)
        {
            if (!Metrics.TryGetValue(name, out var state))
            {
                state = new MetricState();
                Metrics[name] = state;
            }

            state.LastMilliseconds = milliseconds;
            state.TotalCount++;
            state.TotalMilliseconds += milliseconds;
            if (milliseconds > state.MaxMilliseconds) state.MaxMilliseconds = milliseconds;
            state.Recent.Enqueue(milliseconds);
            while (state.Recent.Count > WindowSize) state.Recent.Dequeue();

            long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (state.ActiveSecond == 0) state.ActiveSecond = second;
            if (second != state.ActiveSecond)
            {
                if (state.ActiveSecondCount > 0)
                    state.RateBuckets.Enqueue(new RateBucket(state.ActiveSecond, state.ActiveSecondCount, state.ActiveSecondMilliseconds));

                state.ActiveSecond = second;
                state.ActiveSecondCount = 0;
                state.ActiveSecondMilliseconds = 0;

                while (state.RateBuckets.Count > 0 &&
                       (second - state.RateBuckets.Peek().UnixSecond >= RateWindowSeconds || state.RateBuckets.Count > MaxRateBuckets))
                    state.RateBuckets.Dequeue();
            }

            state.ActiveSecondCount++;
            state.ActiveSecondMilliseconds += milliseconds;
        }
    }

    public static IReadOnlyList<SuiteTimingMetricSnapshot> GetSnapshot()
    {
        lock (Gate)
        {
            return Metrics
                .Select(pair => BuildSnapshot(pair.Key, pair.Value))
                .OrderByDescending(x => x.P95Milliseconds)
                .ThenByDescending(x => x.AverageMilliseconds)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static SuiteTimingMetricSnapshot BuildSnapshot(string name, MetricState state)
    {
        double[] recent = state.Recent.ToArray();
        Array.Sort(recent);
        double recentAverage = recent.Length == 0 ? 0 : recent.Average();
        double p95 = recent.Length == 0 ? 0 : recent[(int)Math.Min(recent.Length - 1, Math.Ceiling(recent.Length * 0.95) - 1)];

        long nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long recentCalls = 0;
        double recentMilliseconds = 0;

        foreach (var bucket in state.RateBuckets)
        {
            if (nowSecond - bucket.UnixSecond >= RateWindowSeconds) continue;
            recentCalls += bucket.Count;
            recentMilliseconds += bucket.Milliseconds;
        }

        if (state.ActiveSecond > 0 && nowSecond - state.ActiveSecond < RateWindowSeconds)
        {
            recentCalls += state.ActiveSecondCount;
            recentMilliseconds += state.ActiveSecondMilliseconds;
        }

        return new SuiteTimingMetricSnapshot
        {
            Name = name,
            Samples = recent.Length,
            TotalCount = state.TotalCount,
            LastMilliseconds = state.LastMilliseconds,
            AverageMilliseconds = recentAverage,
            P95Milliseconds = p95,
            MaxMilliseconds = state.MaxMilliseconds,
            CallsPerMinute = recentCalls,
            TimePerMinuteMilliseconds = recentMilliseconds
        };
    }

    private sealed class MetricState
    {
        public Queue<double> Recent { get; } = new();
        public Queue<RateBucket> RateBuckets { get; } = new();
        public long TotalCount { get; set; }
        public double TotalMilliseconds { get; set; }
        public double LastMilliseconds { get; set; }
        public double MaxMilliseconds { get; set; }
        public long ActiveSecond { get; set; }
        public long ActiveSecondCount { get; set; }
        public double ActiveSecondMilliseconds { get; set; }
    }

    private readonly record struct RateBucket(long UnixSecond, long Count, double Milliseconds);
}

public sealed class SuiteTimingMetricSnapshot
{
    public string Name { get; set; } = "";
    public int Samples { get; set; }
    public long TotalCount { get; set; }
    public double LastMilliseconds { get; set; }
    public double AverageMilliseconds { get; set; }
    public double P95Milliseconds { get; set; }
    public double MaxMilliseconds { get; set; }
    public long CallsPerMinute { get; set; }
    public double TimePerMinuteMilliseconds { get; set; }
}
