using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BlockLogger.WebMap;

// A public DTO deliberately has no player identities or unresolved destination coordinates.
public sealed record WebMapTranslocatorMarker
{
    public string Id { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public string State { get; init; } = "broken";
    public bool HasTarget { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TargetX { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TargetY { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TargetZ { get; init; }
}

public sealed record WebMapTranslocatorRecord
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public string State { get; init; } = "broken";
    // Only Observe creates a discovery. Learning TargetId NEVER creates a destination record.
    public string? TargetId { get; init; }
    public long ObservedUnixMilliseconds { get; init; }
    public string Id => WebMapTranslocatorRules.IdFor(X, Y, Z);
}

public sealed class WebMapTranslocatorDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string WorldId { get; set; } = "";
    public List<WebMapTranslocatorRecord> Translocators { get; set; } = new();
}

public static class WebMapTranslocatorRules
{
    public const int MaximumRecords = 5000;
    public static string IdFor(int x, int y, int z) => FormattableString.Invariant($"{x},{y},{z}");
    public static bool Near(int ax, int ay, int az, double x, double y, double z, double radius)
    {
        double dx = ax + .5 - x, dy = ay + .5 - y, dz = az + .5 - z;
        return double.IsFinite(dx) && double.IsFinite(dy) && double.IsFinite(dz) &&
            dx * dx + dy * dy + dz * dz <= radius * radius;
    }
}

// Immutable values let the disk worker take a short bounded snapshot, then serialize outside the lock.
public sealed class WebMapTranslocatorRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, WebMapTranslocatorRecord> records = new(StringComparer.Ordinal);
    public long Version { get; private set; }
    public int Count { get { lock(gate)return records.Count; } }
    public bool Observe(int x, int y, int z, bool repaired, BlockPos? target)
    {
        var next = new WebMapTranslocatorRecord { X=x, Y=y, Z=z, State=repaired?"active":"broken",
            TargetId=repaired && target?.dimension == 0 ? WebMapTranslocatorRules.IdFor(target.X,target.Y,target.Z) : null };
        lock (gate)
        {
            if (records.TryGetValue(next.Id, out var old))
            {
                if (old.State == next.State && old.TargetId == next.TargetId) return false;
            }
            else if (records.Count >= WebMapTranslocatorRules.MaximumRecords) return false;
            records[next.Id] = next with { ObservedUnixMilliseconds=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            Version++; return true;
        }
    }
    public bool Remove(int x, int y, int z)
    {
        lock (gate) { if (!records.Remove(WebMapTranslocatorRules.IdFor(x,y,z))) return false; Version++; return true; }
    }
    public int Clear() { lock (gate) { int n=records.Count; records.Clear(); Version++; return n; } }
    public WebMapTranslocatorRecord[] Export() { lock (gate) return records.Values.ToArray(); }
    public void Restore(WebMapTranslocatorDocument document, string worldId)
    {
        if (document.SchemaVersion != 2 || document.WorldId != worldId) return;
        lock (gate)
            foreach (var r in (document.Translocators ?? new()).Take(WebMapTranslocatorRules.MaximumRecords))
                if (r != null && r.ObservedUnixMilliseconds > 0 && r.Y >= 0 &&
                    r.State is "active" or "broken" && !records.ContainsKey(r.Id) && records.Count < WebMapTranslocatorRules.MaximumRecords)
                    records[r.Id] = r;
    }
    public List<WebMapTranslocatorMarker> Snapshot()
    {
        lock (gate)
            return records.Values.Select(r =>
            {
                records.TryGetValue(r.TargetId ?? "", out var target);
                bool linked = r.State == "active" && target?.State == "active" && target.Id != r.Id;
                return new WebMapTranslocatorMarker { Id=r.Id,X=r.X,Y=r.Y,Z=r.Z,State=r.State,HasTarget=linked,
                    TargetX=linked?target!.X:null,TargetY=linked?target!.Y:null,TargetZ=linked?target!.Z:null };
            }).OrderBy(r=>r.Id,StringComparer.Ordinal).ToList();
    }
}

/// <summary>Shared public discoveries, from actual player proximity/selection/interaction/placement.
/// No teleport-jump inference, target-side world reads, world generation or chunk loading.</summary>
public sealed class WebMapTranslocatorTracker : IDisposable
{
    public const int ReadsPerTick = 128;
    private readonly ICoreServerAPI api;
    private readonly Func<int> discoveryRadius;
    private readonly WebMapTranslocatorRegistry registry = new();
    private readonly string worldId, filePath;
    private readonly SemaphoreSlim saveSignal = new(0,1);
    private readonly Task worker;
    private readonly object lifecycle = new();
    private readonly BlockPos cursor = new(0,0,0,0);
    private int playerCursor;
    private long scanRound, lastObservationErrorMs;
    private sealed class ScanState { public int X,Y,Z,Cell,Radius; }
    private readonly Dictionary<string,ScanState> scans = new(StringComparer.Ordinal);
    private long tick = -1;
    private volatile bool enabled, disposed, dirty, loaded;
    private string storageIssue = "Loading discoveries", observationIssue = "";
    public string StorageIssue => Volatile.Read(ref storageIssue) is { Length: > 0 } issue ? issue : Volatile.Read(ref observationIssue) is { Length: > 0 } observed ? observed : registry.Count >= WebMapTranslocatorRules.MaximumRecords ? "Discovery limit reached (5000)" : "";
    public long Version => registry.Version;
    public WebMapTranslocatorTracker(ICoreServerAPI api,string dataRoot,Func<int> discoveryRadius)
    {
        this.api=api;this.discoveryRadius=discoveryRadius;
        worldId=api.World.SavegameIdentifier ?? "";
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worldId))).ToLowerInvariant()[..16];
        filePath=Path.Combine(dataRoot,$"translocators-v2-{hash}.json");
        if(worldId.Length==0){storageIssue="Tracking unavailable: missing world identity";worker=Task.CompletedTask;}
        else worker=Task.Run(PersistAsync);
    }
    public void Start()
    {
        lock(lifecycle)
        {
            if(enabled||disposed||worldId.Length==0)return;
            enabled=true;
            try
            {
                tick=api.Event.RegisterGameTickListener(_=>Tick(),250);
                api.Event.DidUseBlock+=OnUse;api.Event.DidPlaceBlock+=OnPlace;api.Event.DidBreakBlock+=OnBreak;api.Event.PlayerDisconnect+=OnDisconnect;
            }
            catch(Exception ex){Stop();Report(ex);}
        }
    }
    public void Stop()
    {
        lock(lifecycle)
        {
            if(!enabled)return;enabled=false;
            try { if(tick>=0)api.Event.UnregisterGameTickListener(tick); } catch(Exception ex){Report(ex);} tick=-1;
            try { api.Event.DidUseBlock-=OnUse;api.Event.DidPlaceBlock-=OnPlace;api.Event.DidBreakBlock-=OnBreak;api.Event.PlayerDisconnect-=OnDisconnect; } catch(Exception ex){Report(ex);}
            scans.Clear();
        }
    }
    public List<WebMapTranslocatorMarker> Snapshot()=>registry.Snapshot();
    public int Clear(){int n=registry.Clear();Changed();return n;}
    private static bool Eligible(IServerPlayer? p)=>p?.Entity!=null && p.ConnectionState==EnumClientState.Playing &&
        p.Entity.Pos.Dimension==0 && double.IsFinite(p.Entity.Pos.X) && double.IsFinite(p.Entity.Pos.Y) && double.IsFinite(p.Entity.Pos.Z) &&
        Math.Abs(p.Entity.Pos.X)<int.MaxValue-32d && Math.Abs(p.Entity.Pos.Z)<int.MaxValue-32d && p.Entity.Pos.Y>=0 && p.Entity.Pos.Y<int.MaxValue-32d &&
        p.WorldData?.CurrentGameMode!=EnumGameMode.Spectator;
    private void OnDisconnect(IServerPlayer player)=>scans.Remove(player.PlayerUID);
    private void OnUse(IServerPlayer player,BlockSelection selection)=>ObserveEvent(player,selection?.Position);
    private void OnPlace(IServerPlayer player,int oldId,BlockSelection selection,ItemStack stack)=>ObserveEvent(player,selection?.Position);
    private void OnBreak(IServerPlayer player,int oldId,BlockSelection selection)
    {
        if(enabled && selection?.Position?.dimension==0 && registry.Remove(selection.Position.X,selection.Position.Y,selection.Position.Z))Changed();
    }
    private void ObserveEvent(IServerPlayer player,BlockPos? pos)
    {
        try
        {
            if(!enabled||!loaded||!Eligible(player)||pos?.dimension!=0)return;
            // Placement/interaction is explicit discovery, including creative placement, but still local.
            if(WebMapTranslocatorRules.Near(pos.X,pos.Y,pos.Z,player.Entity.Pos.X,player.Entity.Pos.Y,player.Entity.Pos.Z,16))TryObserve(pos);
        }
        catch(Exception ex){Report(ex);}
    }
    private void Tick()
    {
        if(!enabled||!loaded||disposed)return;
        bool advance=false;
        try
        {
            var players=api.World.AllOnlinePlayers;
            if(players.Length==0)return;
            // Round robin: one player and at most 128 loaded-block probes per pass, not a cube per player.
            if(playerCursor>=players.Length){playerCursor=0;scanRound++;}
            advance=true;
            if(players[playerCursor] is not IServerPlayer player || !Eligible(player)) return;
            int radius=Math.Clamp(discoveryRadius(),3,16), side=radius*2+1;
            int x=(int)Math.Floor(player.Entity.Pos.X),y=(int)Math.Floor(player.Entity.Pos.Y),z=(int)Math.Floor(player.Entity.Pos.Z);
            if(!scans.TryGetValue(player.PlayerUID,out var scan))
            {
                if(scans.Count>=256)scans.Remove(scans.Keys.First());
                scans[player.PlayerUID]=scan=new ScanState { X=x,Y=y,Z=z,Radius=radius,Cell=(int)((scanRound*127)%(side*side*5)) };
            }
            if(scan.Radius!=radius||Math.Abs((long)scan.X-x)>2||Math.Abs((long)scan.Y-y)>2||Math.Abs((long)scan.Z-z)>2)
            { scan.X=x;scan.Y=y;scan.Z=z;scan.Radius=radius;scan.Cell=0; }
            long start=Stopwatch.GetTimestamp();
            // Looking at a pad discovers it immediately on this player's turn; distant selection is rejected.
            if(player.CurrentBlockSelection?.Position is BlockPos selected && selected.dimension==0 &&
                WebMapTranslocatorRules.Near(selected.X,selected.Y,selected.Z,player.Entity.Pos.X,player.Entity.Pos.Y,player.Entity.Pos.Z,radius))TryObserve(selected);
            int cells=side*side*5;
            for(int reads=1;reads<ReadsPerTick && scan.Cell<cells;reads++,scan.Cell++)
            {
                if(Stopwatch.GetElapsedTime(start).TotalMilliseconds>=2)break;
                int cx=scan.X+scan.Cell%side-radius,cz=scan.Z+(scan.Cell/side)%side-radius,cy=scan.Y+scan.Cell/(side*side)-2;
                cursor.Set(cx,cy,cz);
                if(cy>=0 && WebMapTranslocatorRules.Near(cx,cy,cz,player.Entity.Pos.X,player.Entity.Pos.Y,player.Entity.Pos.Z,radius))TryObserve(cursor);
            }
            if(scan.Cell>=cells)scan.Cell=0;
            Volatile.Write(ref observationIssue,"");
        }
        catch(Exception ex){Report(ex);}
        finally { if(advance)playerCursor++; } // Failed observations must not starve later players.
    }
    private void Report(Exception ex)
    {
        Volatile.Write(ref observationIssue,"Observation skipped: "+ex.GetType().Name);
        long now=Environment.TickCount64;
        if(lastObservationErrorMs!=0 && now-lastObservationErrorMs<60_000)return;
        lastObservationErrorMs=now;
        try{api.Logger.Debug("[ServerEssentials/WebMap] translocator observation skipped ({0}).",ex.GetType().Name);}catch{}
    }
    private void TryObserve(BlockPos pos)
    {
        if(pos.dimension!=0||api.World.BlockAccessor.GetChunkAtBlockPos(pos)==null)return;
        // Official block-entity type/target only. A similarly named mod block is not enough evidence.
        if(api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityStaticTranslocator pad)
        {
            if(registry.Observe(pos.X,pos.Y,pos.Z,pad.FullyRepaired,pad.TargetLocation))Changed();
        }
        else if(registry.Remove(pos.X,pos.Y,pos.Z))Changed();
    }
    private void Changed(){dirty=true;try{saveSignal.Release();}catch(SemaphoreFullException){}}
    private async Task PersistAsync()
    {
        try
        {
            if(File.Exists(filePath))
            {
                var info=new FileInfo(filePath);
                if(info.Length>4*1024*1024)throw new InvalidDataException("Discovery file too large");
                var doc=JsonSerializer.Deserialize<WebMapTranslocatorDocument>(await File.ReadAllBytesAsync(filePath).ConfigureAwait(false));
                if(doc==null || doc.SchemaVersion!=2 || doc.WorldId!=worldId)throw new InvalidDataException("Discovery schema/world mismatch");
                registry.Restore(doc,worldId);
            }
            Volatile.Write(ref storageIssue,"");
        }
        catch(Exception ex)
        {
            Volatile.Write(ref storageIssue,"Discovery load failed: "+ex.GetType().Name);
            try { File.Copy(filePath,filePath+".unreadable-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")); } catch { }
        }
        finally{loaded=true;}
        // Single writer lives across enable/disable toggles. No game API, JSON work or file I/O on game ticks.
        while(true)
        {
            await saveSignal.WaitAsync().ConfigureAwait(false);
            if(!disposed)await Task.Delay(1000).ConfigureAwait(false);
            if(dirty)
            {
                dirty=false;
                try
                {
                    var document=new WebMapTranslocatorDocument {WorldId=worldId,Translocators=registry.Export().ToList()};
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    string temporary=filePath+".tmp";
                    await File.WriteAllBytesAsync(temporary,JsonSerializer.SerializeToUtf8Bytes(document)).ConfigureAwait(false);
                    File.Move(temporary,filePath,true);Volatile.Write(ref storageIssue,"");
                }
                catch(Exception ex){dirty=true;Volatile.Write(ref storageIssue,"Discovery save failed: "+ex.GetType().Name);
                    if(!disposed){await Task.Delay(5000).ConfigureAwait(false);Changed();}}
            }
            if(disposed)return;
        }
    }
    public void Dispose()
    {
        if(disposed)return;Stop();disposed=true;try{saveSignal.Release();}catch(SemaphoreFullException){}
        // Mod shutdown only; live toggles call Stop instead and never wait for disk.
        try{worker.Wait(TimeSpan.FromSeconds(2));}catch{}
    }
}
