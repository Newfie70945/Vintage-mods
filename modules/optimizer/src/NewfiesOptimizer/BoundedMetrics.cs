namespace NewfiesOptimizer;

// Fixed logarithmic-free 1 ms histogram; engine tick source itself is integer milliseconds.
public sealed class DurationHistogram
{
    private readonly long[] bins;
    private readonly double bucketWidthMs;
    public long Count { get; private set; }
    public double TotalMs { get; private set; }
    public double WorstMs { get; private set; }
    public long OverflowCount { get; private set; }
    public DurationHistogram(int maximumMs = 8192, double bucketWidthMs = 1)
    {
        this.bucketWidthMs = bucketWidthMs;
        bins = new long[(int)(maximumMs / bucketWidthMs) + 1];
    }
    public void Record(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0) return;
        Count++; TotalMs += ms; WorstMs = Math.Max(ms, WorstMs);
        int bucket = ms / bucketWidthMs >= int.MaxValue ? int.MaxValue : (int)(ms / bucketWidthMs);
        if (bucket >= bins.Length) { OverflowCount++; bins[^1]++; }
        else bins[bucket]++;
    }
    public double? Percentile(double p)
    {
        if (Count == 0) return null;
        var target = Math.Max(1, (long)Math.Ceiling(Math.Clamp(p, 0, 1) * Count));
        long sum = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            sum += bins[i];
            if (sum >= target) return i == bins.Length - 1 && OverflowCount > 0 ? null : i * bucketWidthMs;
        }
        return null;
    }
    public TickSummary Summarize(double durationSeconds) => new(Count, Count > 0 ? TotalMs / Count : null,
        Percentile(.5), Percentile(.95), Percentile(.99), Count > 0 ? WorstMs : null, OverflowCount,
        durationSeconds > 0 && Count > 0 ? Count / durationSeconds : null,
        durationSeconds > 0 && Count > 0 ? TotalMs / (durationSeconds * 10) : null,
        "Official ServerMain.StatsCollector pre-sleep work; integer milliseconds. Effective TPS is completed ticks per wall second, not 1000 / processing time. Percentiles above histogram range are null.");
}

public sealed class BoundedRing<T>
{
    private readonly T[] buffer;
    private int next;
    public int Count { get; private set; }
    public long Overwritten { get; private set; }
    public BoundedRing(int capacity) => buffer = new T[Math.Max(1, capacity)];
    public void Add(T value)
    {
        if (Count == buffer.Length) Overwritten++; else Count++;
        buffer[next] = value; next = (next + 1) % buffer.Length;
    }
    public T[] Snapshot()
    {
        var result = new T[Count];
        int first = (next - Count + buffer.Length) % buffer.Length;
        for (int i = 0; i < Count; i++) result[i] = buffer[(first + i) % buffer.Length];
        return result;
    }
}

public sealed class HotspotCounter(string name, string category)
{
    private readonly DurationHistogram durations = new(2048, .25);
    public long Calls;
    public void Record(double ms) => durations.Record(ms);
    public HotspotSummary Snapshot() => new(name, category, Calls, durations.Count, durations.TotalMs,
        durations.Count > 0 ? durations.TotalMs / durations.Count : null, durations.Percentile(.95), durations.Count > 0 ? durations.WorstMs : null,
        "Inclusive sampled calls; overlapping parent/child timings must not be added. Unsampled calls are not extrapolated. Percentile resolution is 0.25 ms, rounded down; overflowing percentiles are unavailable.");
}
