using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.AntiCheat;

// Per-observation, bounded and never retained in a world-wide position cache.
internal sealed class NoclipTerrainEvidence
{
    private readonly Dictionary<(int X,int Y,int Z),string> blocks = new();
    internal bool ComplexGeometry { get; private set; }
    internal bool Truncated { get; private set; }
    internal void Add(int x,int y,int z,string code,bool complex)
    {
        ComplexGeometry |= complex;
        var key=(x,y,z);
        if(blocks.ContainsKey(key))return;
        if(blocks.Count>=128){Truncated=true;return;}
        blocks.Add(key,code);
    }
    internal bool CorrectionCompatible => !ComplexGeometry && !Truncated && blocks.Count>0;
    internal string Describe() => $"uniqueIntersectedBlocks={blocks.Count};uniqueBlockTypes={blocks.Values.Distinct().Count()};complexGeometry={ComplexGeometry};terrainEvidenceTruncated={Truncated};blockTypes={string.Join(",",blocks.Values.Distinct().Take(8))}";
}

internal sealed class NoclipConfirmationWindow
{
    private long first,last;
    private int count;
    internal bool Observe(long now)
    {
        if(count==0 || now<=last || now-last>5000 || now-first>15000){first=last=now;count=1;return false;}
        if(now-last<500)return false;
        last=now;count++;
        return count>=3 && now-first>=1500;
    }
    internal void Reset(){first=last=0;count=0;}
}

// Counts only successful authoritative corrections, never raw collision samples.
internal sealed class NoclipCorrectionHistory
{
    private readonly Queue<long> corrections = new();
    private long last;
    internal int Count => corrections.Count;
    internal bool Record(long now,int threshold,int decaySeconds,int minimumIntervalSeconds)
    {
        if(corrections.Count>0 && now<last){corrections.Clear();last=0;}
        long expiry=now-(long)Math.Clamp(decaySeconds,30,3600)*1000;
        while(corrections.Count>0 && corrections.Peek()<=expiry)corrections.Dequeue();
        if(corrections.Count>0 && now-last<(long)Math.Clamp(minimumIntervalSeconds,5,300)*1000)return false;
        if(corrections.Count>=50)corrections.Dequeue();
        corrections.Enqueue(now);last=now;
        return corrections.Count>=Math.Clamp(threshold,2,50);
    }
}

// A teleport API return is not evidence that the correction reached its destination.
internal sealed class PendingNoclipCorrection
{
    internal bool Active { get; private set; }
    private long requested,entity;
    private int dimension;
    private double x,y,z;
    internal void Stage(long now,long entityId,int dim,double targetX,double targetY,double targetZ)
    {requested=now;entity=entityId;dimension=dim;x=targetX;y=targetY;z=targetZ;Active=true;}
    internal bool Observe(long now,long entityId,int dim,double actualX,double actualY,double actualZ,bool eligible)
    {
        if(!Active)return false;
        if(!eligible || entityId!=entity || dim!=dimension || now<requested || now-requested>3000 ||
            !double.IsFinite(actualX)||!double.IsFinite(actualY)||!double.IsFinite(actualZ)){Reset();return false;}
        if(now-requested<75)return false;
        double dx=actualX-x,dy=actualY-y,dz=actualZ-z;
        if(dx*dx+dy*dy+dz*dz>0.75*0.75)return false;
        Reset();return true;
    }
    internal void Reset()=>Active=false;
}
