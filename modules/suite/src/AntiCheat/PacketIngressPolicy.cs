using System;

namespace BlockLogger.AntiCheat;

/// <summary>Allocation-free protocol invariants shared by the ingress hooks and regression tests.</summary>
internal static class PacketIngressPolicy
{
    internal static int BoundViewDistanceBlocks(int requested, int configuredMaximum, int serverMaximumChunks, int chunkSize)
    {
        int maximum = Math.Clamp(configuredMaximum, 64, 4096);
        if (serverMaximumChunks > 0 && chunkSize > 0)
            maximum = (int)Math.Min(maximum, Math.Min(int.MaxValue, (long)serverMaximumChunks * chunkSize));
        maximum = Math.Max(32, maximum);
        return Math.Clamp(requested, 32, maximum);
    }

    internal static bool IsValidCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= int.MaxValue - 1024.0;

    internal static bool IsValidBlockId(int blockId, int registryCount) => blockId >= 0 && blockId < registryCount;

    internal static bool IsValidFace(int face) => face >= 0 && face <= 5;

    // Modded selection boxes can legitimately extend beyond a block's unit cube. This is
    // a protocol sanity bound, not proof of a ray hit; server reach uses the target geometry.
    internal static bool IsValidHit(double value) => double.IsFinite(value) && value >= -16.0 && value <= 16.0;
}

/// <summary>
/// Monotonic-clock token bucket. It stores no per-packet history, accepts legitimate short
/// bursts, and never grants extra tokens when a clock regresses. Callers serialize access.
/// </summary>
internal sealed class PacketIngressBudget
{
    private double tokens;
    private long lastTimestamp;
    private bool initialized;
    private long accepted, rejected;
    private int rate, capacity;
    internal PacketIngressBudgetSnapshot Snapshot => new(accepted,rejected,tokens,rate,capacity);

    internal bool TryConsume(long monotonicMilliseconds, int perSecond, int burst)
    {
        perSecond = Math.Max(1, perSecond);
        burst = Math.Max(1, burst);
        rate = perSecond; capacity = burst;
        if (!initialized)
        {
            initialized = true;
            tokens = burst;
            lastTimestamp = monotonicMilliseconds;
        }
        else if (monotonicMilliseconds > lastTimestamp)
        {
            tokens = Math.Min(burst, tokens + (monotonicMilliseconds - lastTimestamp) * (double)perSecond / 1000.0);
            lastTimestamp = monotonicMilliseconds;
        }
        tokens = Math.Min(tokens, burst);
        if (tokens < 1.0) { if(rejected<long.MaxValue)rejected++; return false; }
        if(accepted<long.MaxValue)accepted++;
        tokens -= 1.0;
        return true;
    }
}

internal readonly record struct PacketIngressBudgetSnapshot(long Accepted,long Dropped,double Tokens,int Rate,int Burst)
{
    internal string Evidence => FormattableString.Invariant($"acceptedTotal={Accepted};droppedTotal={Dropped};tokensRemaining={Tokens:0.00};configuredRate={Rate};configuredBurst={Burst}");
}
