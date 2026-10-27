using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BlockLogger.AntiCheat;

[Flags]
public enum ServerProtectionScope { None=0, Movement=1, Mining=2, Combat=4, Inventory=8, BlockNuker=16 }
public sealed class ServerProtectionExemption
{
    public string PlayerUid {get;set;}="";
    public string Scope {get;set;}="";
    public string Reason {get;set;}="";
    public string GrantedBy {get;set;}="";
    public long ExpiresUtc {get;set;}
}
internal static class ServerProtectionExemptions
{
    private static readonly ConcurrentDictionary<(string,ServerProtectionScope),ServerProtectionExemption> entries=new();
    internal static bool TryGrant(string uid,ServerProtectionScope scope,int seconds,string reason,string actor,long now)
    {
        if(string.IsNullOrWhiteSpace(uid)||!Enum.IsDefined(scope)||scope==ServerProtectionScope.None||seconds<1||seconds>3600||string.IsNullOrWhiteSpace(reason)||reason.Length>160)return false;
        foreach(var pair in entries)if(pair.Value.ExpiresUtc<=now)entries.TryRemove(pair.Key,out _);
        var key=(uid,scope);
        if(entries.Count>=256&&!entries.ContainsKey(key))return false;
        entries[key]=new(){PlayerUid=uid,Scope=scope.ToString(),Reason=reason,GrantedBy=actor,ExpiresUtc=now+seconds*1000L};return true;
    }
    internal static bool Has(string uid,ServerProtectionScope scope,long now)
    {
        if(!entries.TryGetValue((uid,scope),out var grant))return false;
        if(grant.ExpiresUtc>now)return true;
        entries.TryRemove((uid,scope),out _);return false;
    }
    internal static void Revoke(string uid,ServerProtectionScope scope)=>entries.TryRemove((uid,scope),out _);
    internal static List<ServerProtectionExemption> Snapshot(string uid,long now)=>entries.Values.Where(x=>x.PlayerUid==uid&&x.ExpiresUtc>now).Select(x=>new ServerProtectionExemption{PlayerUid=x.PlayerUid,Scope=x.Scope,Reason=x.Reason,GrantedBy=x.GrantedBy,ExpiresUtc=x.ExpiresUtc}).ToList();
    internal static void Clear()=>entries.Clear();
}

internal sealed class CorrectionRecoveryGuard
{
    private long first,until;
    private int failures;
    internal bool Suspended(long now)=>now<until;
    internal bool Fail(long now)
    {
        if(Suspended(now))return false;
        if(failures==0||now<first||now-first>30000){first=now;failures=0;}
        if(++failures<3)return false;
        until=now+60000;failures=0;return true;
    }
    internal void Success(){first=0;failures=0;}
}

internal sealed class ActionRateWindow
{
    private readonly Queue<long> times=new();
    private long last;
    internal int Observe(long now,int windowMs=1000)
    {
        if(now<last)times.Clear();last=now;
        while(times.Count>0&&now-times.Peek()>=windowMs)times.Dequeue();
        if(times.Count>=256)times.Dequeue();times.Enqueue(now);return times.Count;
    }
    internal void Reset()=>times.Clear();
}

public sealed class AntiCheatServerEvent
{
    public long Utc {get;set;}
    public string Code {get;set;}="";
    public string Summary {get;set;}="";
    public string Evidence {get;set;}="";
}

// Client damage packets are only start markers, never proof of completed mining work.
internal sealed class MiningRequestObservation
{
    private bool active;
    private int x,y,z,block;
    private string tool="";
    private long started;
    internal void Start(long now,int bx,int by,int bz,int blockId,string heldTool)
    {
        if(!active||x!=bx||y!=by||z!=bz||block!=blockId||tool!=heldTool||now<started||now-started>30000)
        {x=bx;y=by;z=bz;block=blockId;tool=heldTool;started=now;active=true;}
    }
    internal void Reset()=>active=false;
    internal long? Elapsed(long now,int bx,int by,int bz,int blockId,string heldTool)=>active&&x==bx&&y==by&&z==bz&&block==blockId&&tool==heldTool&&now>=started&&now-started<=30000?now-started:null;
}
