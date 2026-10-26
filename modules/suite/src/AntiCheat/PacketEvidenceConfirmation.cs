namespace BlockLogger.AntiCheat;

// Counts sustained over-budget activity, not packets released in one delayed
// server tick. Each connection owns one fixed-size window per resource budget.
internal sealed class PacketFloodConfirmation
{
    private bool started;
    private long first,last,lastCounted;
    private int count;
    internal (int Windows,long SpanMs) Evidence => (count,started?Math.Max(0,last-first):0);
    internal bool Observe(long now,bool timingReliable=true)
    {
        if(!timingReliable){started=false;count=0;return false;}
        if(!started||now<last||now-last>1500)
        {started=true;first=now;lastCounted=now;count=1;}
        else if(now-lastCounted>=100)
        {lastCounted=now;count=Math.Min(1_000_000,count+1);}
        last=now;
        // Distinct observation intervals, not the number of packets in one drained burst.
        return now-first>=3000 && count>=10;
    }
}

internal static class PacketReachEvidence
{
    internal static bool IsUsableSample(long now,long timestamp,int rewindMs,int dimension,int sampleDimension,long entityId,long sampleEntityId) =>
        timestamp>=0 && now>=timestamp && now-timestamp<=Math.Max(0,rewindMs) && dimension==sampleDimension && entityId==sampleEntityId;
    internal static double PingMilliseconds(double seconds) => double.IsFinite(seconds)&&seconds>=0?Math.Clamp(seconds*1000.0,0.0,1000.0):1000.0;
    internal static bool Eligible(string action,int strikes,int distinctTargets,long spanMs,int requiredStrikes,int requiredTargets,bool timingReliable) =>
        action.Equals("break",StringComparison.OrdinalIgnoreCase) && timingReliable &&
        strikes>=Math.Max(3,requiredStrikes) && distinctTargets>=Math.Max(2,requiredTargets) && spanMs>=750;
}

internal static class PacketTimingHealth
{
    internal static double EffectiveTickMs(double smoothed,double elapsed,double target,double maximumMultiplier) =>
        Math.Max(smoothed,Math.Clamp(elapsed,target,target*Math.Max(4.0,maximumMultiplier*2.0)));
}

internal static class PacketReachGeometry
{
    internal static bool ValidBox(double x1,double y1,double z1,double x2,double y2,double z2) =>
        double.IsFinite(x1)&&double.IsFinite(y1)&&double.IsFinite(z1)&&double.IsFinite(x2)&&double.IsFinite(y2)&&double.IsFinite(z2)&&
        x1>=-32&&y1>=-32&&z1>=-32&&x2<=33&&y2<=33&&z2<=33&&x2>=x1&&y2>=y1&&z2>=z1;
    internal static double Distance(double x,double y,double z,double x1,double y1,double z1,double x2,double y2,double z2)
    {
        double dx=x-Math.Clamp(x,x1,x2),dy=y-Math.Clamp(y,y1,y2),dz=z-Math.Clamp(z,z1,z2);
        return Math.Sqrt(dx*dx+dy*dy+dz*dz);
    }
}

internal static class PacketMovementEvidence
{
    internal static bool FreshPosition(long now,long received,int maximumAgeMs=1500) => received>=0 && now>=received && now-received<=maximumAgeMs;
    internal static bool ReliableCollisionTiming(bool healthEnabled,bool ready,bool degraded,bool critical,bool freshPosition) =>
        freshPosition && (!healthEnabled || ready&&!degraded&&!critical);
}
