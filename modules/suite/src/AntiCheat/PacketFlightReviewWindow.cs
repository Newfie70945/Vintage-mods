using System;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Conservative confirmation of a continuously unsupported, non-falling observation window.
/// This is advisory evidence, never sufficient to infer intent or enforce punishment.
/// </summary>
internal sealed class PacketFlightReviewWindow
{
    private bool initialized;
    private bool reported;
    private long startUtc;
    private long lastUtc;
    private double startY;
    private long observedEntityId;
    private int observedDimension;

    internal long DurationMs { get; private set; }
    internal double Rise { get; private set; }

    internal bool Observe(long now, double y, bool eligible, int confirmationMs, int maximumGapMs, double maximumDrop = 0.5,long entityId = 0,int dimension = 0)
    {
        if (!eligible || !double.IsFinite(y))
        {
            Reset();
            return false;
        }
        if (!initialized || observedEntityId!=entityId || observedDimension!=dimension || now <= lastUtc || now - lastUtc > maximumGapMs || y < startY - maximumDrop)
        {
            initialized = true;
            reported = false;
            startUtc = lastUtc = now;
            startY = y; observedEntityId=entityId; observedDimension=dimension;
            DurationMs = 0;
            Rise = 0;
            return false;
        }
        lastUtc = now;
        DurationMs = now - startUtc;
        Rise = y - startY;
        if (reported || DurationMs < Math.Max(3000, confirmationMs)) return false;
        reported = true;
        return true;
    }

    internal void Reset()
    {
        initialized = reported = false;
        startUtc = lastUtc = 0;
        startY = Rise = 0; observedEntityId=0;observedDimension=0;
        DurationMs = 0;
    }
}
