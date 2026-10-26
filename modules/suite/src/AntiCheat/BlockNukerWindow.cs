using System;
using System.Collections.Generic;
namespace BlockLogger.AntiCheat;

// Vanilla break requests only: secondary server-side vein/tree/tool breaks do not enter here.
internal sealed class BlockNukerWindow
{
    private readonly Queue<(long Utc,int X,int Y,int Z)> targets=new();
    private readonly HashSet<(int X,int Y,int Z)> unique=new();
    private long last,lastBurst,firstBurst;
    private int bursts;
    internal (bool Allow,bool Kick,int Distinct) Observe(long now,int x,int y,int z,int limit,bool reliable)
    {
        limit=Math.Clamp(limit,5,200);
        if(now<last){targets.Clear();unique.Clear();bursts=0;}
        last=now;
        while(targets.Count>0 && now-targets.Peek().Utc>=1000){var old=targets.Dequeue();unique.Remove((old.X,old.Y,old.Z));}
        var key=(x,y,z);
        if(!unique.Contains(key) && targets.Count<=limit){targets.Enqueue((now,x,y,z));unique.Add(key);}
        bool allow=unique.Count<=limit;
        if(!reliable){bursts=0;return(allow,false,unique.Count);}
        if(allow)return(true,false,unique.Count);
        if(bursts==0 || now-firstBurst>15000){firstBurst=lastBurst=now;bursts=1;}
        else if(now-lastBurst>=1000){lastBurst=now;bursts++;}
        return(false,bursts>=2,unique.Count);
    }
}
