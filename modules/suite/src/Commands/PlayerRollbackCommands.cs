using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BlockLogger.Models;
using BlockLogger.Services;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Commands;

public sealed class PlayerRollbackCommands : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly LogStorageService storage;
    private readonly Action<int,int,int>? dirty;
    private readonly Dictionary<string,Plan> plans=new();
    private readonly HashSet<string> changed=new();
    private readonly HashSet<string> trackedCells=new();
    private bool trackAllDuringRead;
    private long maintenanceTick, lastReplyFailureTicks;
    private readonly HashSet<string> receipts=new();
    private readonly List<(InventoryBase Inventory,Action<int> Handler)> watchers=new();
    private readonly string receiptPath;
    private bool busy,receiptReady;
    private string receiptFailure = "Receipt index is initializing; try again shortly.";
    private readonly RecoveryReceiptIndex receiptIndex;
    private volatile bool disposed,cancelRequested;
    private CancellationTokenSource? operationCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private string activeAdmin="";
    private sealed record Prepared(RecoveryOperation Operation,string LiveHash);
    private sealed record Plan(string Token,string Uid,bool Area,DateTime From,DateTime To,DateTime Expires,bool Inventories,bool Entities,bool IncludeClaims,RecoveryCell? Center,int Radius,List<Prepared> Changes)
    {
        public bool SingleBlock => Area && Center != null && Radius == 0;
        public string Mode => SingleBlock ? "block" : Area ? "area" : "player";
    }
    public PlayerRollbackCommands(ICoreServerAPI api,LogStorageService storage,Action<int,int,int>? dirty)
    {this.api=api;this.storage=storage;this.dirty=dirty;receiptPath=storage.DatabasePath+".recovery-receipts.jsonl";receiptIndex=new RecoveryReceiptIndex(receiptPath);}
    public void Register()
    {
        _ = InitializeReceiptsAsync();
        maintenanceTick = api.Event.RegisterGameTickListener(_ => PruneExpiredPlans(), 30_000);
        api.Permissions.RegisterPrivilege("newfies.rollback","Preview and execute historical recovery",true);
#pragma warning disable CS0618
        api.RegisterCommand("rollback","Recover historical block, container, player or area changes",RollbackChatText.Encode("block <time> [at X Y Z] [dimension N] OR preview player <name|uid> <time> [radius N] OR preview area <radius> <time>; optional --inventories --replace-entities; execute <token>; cancel"),Command,"newfies.rollback");
#pragma warning restore CS0618
        api.Event.DidBreakBlock+=Break;api.Event.DidPlaceBlock+=Place;api.Event.DidUseBlock+=Use;
    }
    private void Reply(IServerPlayer p,int g,string text,bool error=false)
    {
        try { p.SendMessage(g,RollbackChatText.Encode(text),error?EnumChatType.CommandError:EnumChatType.CommandSuccess); }
        catch(Exception ex)
        {
            // A disconnect must not throw out of a game-thread callback and leave
            // recovery busy, or recursively fail while trying to send an error.
            long now=DateTime.UtcNow.Ticks;
            if(now-lastReplyFailureTicks>TimeSpan.FromSeconds(10).Ticks)
            {lastReplyFailureTicks=now;api.Logger.Warning("[Recovery] Private reply failed ({0}); operation state and journal remain authoritative.",ex.GetType().Name);}
        }
    }
    private void Touch(BlockSelection s)
    {
        if(s?.Position==null || !busy && plans.Count==0)return;
        var p=s.Position; string key = new RecoveryCell(p.dimension.ToString(CultureInfo.InvariantCulture),p.X,p.Y,p.Z).Key;
        if (!trackAllDuringRead && !trackedCells.Contains(key)) return;
        if (changed.Count >= 131072 && !changed.Contains(key))
        { cancelRequested=true; operationCancellation?.Cancel(); return; }
        changed.Add(key);
    }
    private void Break(IServerPlayer p,int id,BlockSelection s)=>Touch(s);
    private void Place(IServerPlayer p,int id,BlockSelection s,ItemStack stack)=>Touch(s);
    private void Use(IServerPlayer p,BlockSelection s)=>Touch(s);
    private void PruneExpiredPlans()
    {
        if (busy) return;
        foreach (var key in plans.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray()) plans.Remove(key);
        if (plans.Count == 0) { ClearWatchers(); changed.Clear(); trackedCells.Clear(); receipts.Clear(); }
    }
    private void ClearWatchers()
    {foreach(var (inventory,handler) in watchers)inventory.SlotModified-=handler;watchers.Clear();}
    private void Watch(RecoveryOperation op)
    {
        var pos=Position(op.Cell);
        if(pos!=null && api.World.BlockAccessor.GetBlockEntity(pos) is IBlockEntityContainer container && container.Inventory is InventoryBase inv)
        {
            var key=op.Cell.Key;Action<int> handler=_=>changed.Add(key);inv.SlotModified+=handler;watchers.Add((inv,handler));
        }
    }
    private void Command(IServerPlayer p,int g,CmdArgs args)
    {
        if(disposed)return;
        if(!receiptReady){Reply(p,g,receiptFailure,true);return;}
        var mode=args.PopWord()?.ToLowerInvariant();
        if(mode=="cancel")
        {
            if(busy && activeAdmin!=p.PlayerUID) {Reply(p,g,"Another administrator owns the active operation.",true);return;}
            if(busy) {cancelRequested=true;operationCancellation?.Cancel();Reply(p,g,"Cancellation requested; already applied changes remain journaled.");}
            else {plans.Clear();ClearWatchers();changed.Clear();Reply(p,g,"Preview cleared.");}return;
        }
        if(busy){Reply(p,g,"Recovery operation is running; its owner may /rollback cancel.",true);return;}
        if(mode=="execute")
        {
            var token=args.PopWord();
            if(args.Length!=0 || !plans.TryGetValue(p.PlayerUID,out var plan) || token!=plan.Token || plan.Expires<DateTime.UtcNow)
            {Reply(p,g,"No matching unexpired preview. Request another preview.",true);return;}
            if(Occupied(plan,p.PlayerUID)){Reply(p,g,plan.Area?"Other players must leave the selected area before execution.":"Target must be offline.",true);return;}
            plans.Clear();busy=true;cancelRequested=false;activeAdmin=p.PlayerUID;
            operationCancellation=new();
            _=Execute(p,g,plan);return;
        }
        var scope=mode=="block"?"block":mode=="preview"?args.PopWord()?.ToLowerInvariant():null;
        bool single=scope=="block", area=scope is "area" or "block";
        if(scope is not ("player" or "area" or "block"))
        {Reply(p,g,"/rollback block <time> [at X Y Z] [dimension N]; /rollback preview player <name|uid> <time> [radius N]; /rollback preview area <radius> <time> [--inventories]; /rollback execute <token>",true);return;}
        string? target=null;int radius=0;RecoveryCell? center=null;
        if(single)
        {
            var selected=p.CurrentBlockSelection?.Position;
            if(selected!=null)center=new(selected.dimension.ToString(CultureInfo.InvariantCulture),selected.X,selected.Y,selected.Z);
        }
        else if(area)
        {
            if(!int.TryParse(args.PopWord(),out radius)||radius<1||radius>4096||!EntityPositionResolver.TryGetBlockPos(p.Entity,out var pos))
            {Reply(p,g,"Area radius must be 1..4096; stand at the desired center.",true);return;}
            center=new(pos.dimension.ToString(CultureInfo.InvariantCulture),pos.X,pos.Y,pos.Z);
        }
        else target=args.PopWord();
        var duration=args.PopWord();var to=DateTime.UtcNow;DateTime from;
        if(duration=="from")
        {
            var a=args.PopWord();var separator=args.PopWord();var b=args.PopWord();
            if(!Utc(a,out from)||separator!="to"||!Utc(b,out to)||from>=to||to>DateTime.UtcNow||to-from>TimeSpan.FromDays(7))
            {Reply(p,g,"Use ordered UTC timestamps YYYY-MM-DDTHH:mm:ssZ, maximum 7 days apart.",true);return;}
        }
        else
        {if(!Duration(duration,out var span)){Reply(p,g,"Duration: 1s through 7d; units s/m/h/d.",true);return;}from=to-span;}
        bool inventories=single,entities=false,includeClaims=false,explicitCoordinates=false;
        while(args.Length>0)
        {
            var option=args.PopWord()?.ToLowerInvariant();
            if(single && option=="at" && !explicitCoordinates)
            {
                if(!int.TryParse(args.PopWord(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var x)
                    || !int.TryParse(args.PopWord(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var y)
                    || !int.TryParse(args.PopWord(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var z)
                    || !EntityPositionResolver.TryGetBlockPos(p.Entity,out var playerPos))
                {Reply(p,g,"Use /rollback block 2h at X Y Z [dimension N], with absolute integer coordinates.",true);return;}
                center=new(playerPos.dimension.ToString(CultureInfo.InvariantCulture),x,y,z);explicitCoordinates=true;
            }
            else if(single && option=="dimension" && explicitCoordinates)
            {
                if(!int.TryParse(args.PopWord(),NumberStyles.Integer,CultureInfo.InvariantCulture,out var dimension)||dimension<0)
                {Reply(p,g,"Dimension must be a non-negative integer after at X Y Z.",true);return;}
                center=center! with {Dimension=dimension.ToString(CultureInfo.InvariantCulture)};
            }
            else if(option=="--include-claims")includeClaims=true;
            else if(option=="--inventories")inventories=true;
            else if(option=="--replace-entities" && !single)entities=true;
            else if(!area && option=="radius" && int.TryParse(args.PopWord(),out radius) && radius>=1 && radius<=4096 && EntityPositionResolver.TryGetBlockPos(p.Entity,out var pos))
                center=new(pos.dimension.ToString(CultureInfo.InvariantCulture),pos.X,pos.Y,pos.Z);
            else {Reply(p,g,"Unknown option. Block mode: at X Y Z, dimension N, --include-claims. Area/player mode: --inventories, --replace-entities, --include-claims, or player radius 1..4096.",true);return;}
        }
        if(single && center==null){Reply(p,g,"Look at the block/container, or use /rollback block 2h at X Y Z for a missing block. Coordinates are absolute.",true);return;}
        if(!area && string.IsNullOrWhiteSpace(target)){Reply(p,g,"Specify an exact name or UID.",true);return;}
        plans.Clear();ClearWatchers();changed.Clear();trackedCells.Clear();trackAllDuringRead=true;busy=true;cancelRequested=false;activeAdmin=p.PlayerUID;
        operationCancellation=new();
        _=Preview(p,g,target??"",area,from,to,inventories,entities,includeClaims,center,radius);
        Reply(p,g,"Reading existing event rows and container snapshots; no new history is required.");
    }
    private static bool Utc(string? s,out DateTime utc)=>DateTime.TryParseExact(s,"yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out utc);
    private static bool Duration(string? s,out TimeSpan span)
    {
        span=default;if(s==null||s.Length<2||!double.TryParse(s[..^1],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n))return false;
        double factor=s[^1] switch {'s'=>1,'m'=>60,'h'=>3600,'d'=>86400,_=>0};var seconds=n*factor;
        if(!double.IsFinite(seconds)||seconds<1||seconds>604800)return false;span=TimeSpan.FromSeconds(seconds);return true;
    }
    private static bool InRadius(RecoveryOperation op,RecoveryCell? center,int radius)
    {
        if(center==null)return true;if(op.Cell.Dimension!=center.Dimension)return false;
        double x=(double)op.Cell.X-center.X,y=(double)op.Cell.Y-center.Y,z=(double)op.Cell.Z-center.Z;return x*x+y*y+z*z<=(double)radius*radius;
    }
    private bool Occupied(Plan plan,string adminUid)
    {
        if(!plan.Area)return api.World.AllOnlinePlayers.Any(x=>x.PlayerUID==plan.Uid);
        return api.World.AllOnlinePlayers.Any(x=>x.PlayerUID!=adminUid && EntityPositionResolver.TryGetBlockPos(x.Entity,out var pos)
            && InRadius(new RecoveryOperation {Cell=new(pos.dimension.ToString(CultureInfo.InvariantCulture),pos.X,pos.Y,pos.Z)},plan.Center,plan.Radius));
    }
    private async Task Preview(IServerPlayer p,int g,string target,bool area,DateTime from,DateTime to,bool inventories,bool entities,bool includeClaims,RecoveryCell? center,int radius)
    {
        try
        {
            var cancellation=operationCancellation!.Token;
            var history=await storage.QueryRollbackRecoveryAsync(target,from,to,area?center:null,area?radius:0,cancellation).ConfigureAwait(false);
            var build=await Task.Run(()=>RollbackRecoveryPlanner.Build(history,from,to,inventories,entities,cancellation),cancellation).ConfigureAwait(false);
            var relevantReceipts = await receiptIndex.FindAsync(build.Operations.Select(op => op.ReceiptKey), cancellation).ConfigureAwait(false);
            api.Event.EnqueueMainThreadTask(()=>
            {
                receipts.Clear(); receipts.UnionWith(relevantReceipts);
                trackedCells.Clear(); trackedCells.UnionWith(build.Operations.Select(op => op.Cell.Key));
                changed.IntersectWith(trackedCells); trackAllDuringRead=false;
                var list=new List<Prepared>();int index=0,entityCount=0;
                void Batch(float dt)
                {
                    try
                    {
                        if(disposed){Finish();return;}
                        if(cancelRequested){Finish();Reply(p,g,"Preview cancelled.");return;}
                        var watch=Stopwatch.StartNew();
                        var end=Math.Min(index+64,build.Operations.Count);
                        for(;index<end;index++)
                        {
                            if(index>0 && watch.ElapsedMilliseconds>=4)break;
                            var op=build.Operations[index];if(!InRadius(op,center,radius)){build.Skip("Outside selected radius/dimension");continue;}
                            if(receipts.Contains(op.ReceiptKey)){build.Skip("Already recovered or uncertain prior attempt; receipt retained");continue;}
                            if(changed.Contains(op.Cell.Key)){build.Skip("Live changes during preview");continue;}
                            if(op.Kind=="entity-replacement" && entityCount>=500){build.Skip("Entity replacement cap (500)");continue;}
                            var reason=Validate(op,out var hash,p,includeClaims);if(reason!=null){build.Skip(reason);continue;}
                            list.Add(new(op,hash));Watch(op);if(op.Kind=="entity-replacement")entityCount++;
                        }
                        if(index<build.Operations.Count){api.Event.RegisterCallback(Batch,10);return;}
                        var token=Guid.NewGuid().ToString("N")[..12];
                        var plan=new Plan(token,history.PlayerUid,area,from,to,DateTime.UtcNow.AddMinutes(10),inventories,entities,includeClaims,center,radius,list);
                        var report=storage.DatabasePath+".recovery-preview-"+token+".json";
                        _=PublishPreviewAsync();
                        async Task PublishPreviewAsync()
                        {
                            try
                            {
                                await Task.Run(()=>File.WriteAllText(report,JsonConvert.SerializeObject(new {token,mode=plan.Mode,uid=plan.Uid,center=plan.Center,radius=plan.Radius,from,to,expires=plan.Expires,includeClaims=plan.IncludeClaims,affectedChunks=list.Select(x=>new {x.Operation.Cell.Dimension,x=x.Operation.Cell.X>>5,y=x.Operation.Cell.Y>>5,z=x.Operation.Cell.Z>>5}).Distinct().Count(),inventoryFidelity="codes/counts only; old item attributes not logged",entityFidelity="replacement creatures; old attributes not logged",eligible=list.Select(x=>x.Operation).ToArray(),reasons=build.Reasons},Formatting.Indented))).ConfigureAwait(false);
                                await OnGameThreadAsync(()=>
                                {
                                    if(cancelRequested){Finish();Reply(p,g,"Preview cancelled.");return;}
                                    if(list.Count>0 || !plan.SingleBlock)plans[p.PlayerUID]=plan;busy=false;activeAdmin="";
                                    operationCancellation?.Dispose();operationCancellation=null;
                                    Reply(p,g,$"{(plan.SingleBlock?$"BLOCK: all actors, location={plan.Center!.Key}":plan.Area?$"AREA: all actors, center={plan.Center!.Key}, radius={plan.Radius}":$"UID={plan.Uid}")}; UTC {from:O} through {to:O}. Eligible: {list.Count(x=>x.Operation.Kind=="block")} block locations, {list.Count(x=>x.Operation.Kind=="inventory")} existing inventories, {entityCount} replacement creatures.");
                                    Reply(p,g,plan.IncludeClaims?"Claimed land included explicitly; current game access permissions are still checked.":"Claimed land excluded. Use --include-claims for an intentional claimed-area recovery.");
                                    foreach(var reason in build.Reasons.Take(10))Reply(p,g,$"Skipped: {reason.Value} — {reason.Key}");
                                    if(inventories)Reply(p,g,"Inventory recovery restores saved item codes/counts; original durability, freshness and other attributes were not logged. Taken items/drops are not confiscated.");
                                    if(entities)Reply(p,g,"Entity replacements have default attributes, not the original generation, ownership, names or training.");
                                    if(plan.SingleBlock)
                                    {
                                        foreach(var prepared in list)
                                        {
                                            var op=prepared.Operation;Reply(p,g,$"Block: {op.After} → {op.Before} at {op.Cell.Key}.");
                                            if(op.InventoryBefore!=null)
                                            {
                                                var previous=(op.InventoryAfter??new()).ToDictionary(s=>s.Slot);var desired=op.InventoryBefore.ToDictionary(s=>s.Slot);
                                                foreach(var slot in previous.Keys.Union(desired.Keys).OrderBy(s=>s).Where(s=>previous.GetValueOrDefault(s)!=desired.GetValueOrDefault(s)).Take(12))
                                                {var a=previous.GetValueOrDefault(slot);var b=desired.GetValueOrDefault(slot);Reply(p,g,$"Slot {slot}: {(a==null?"empty":$"{a.Count} × {a.Code}")} → {(b==null?"empty":$"{b.Count} × {b.Code}")}.");}
                                            }
                                        }
                                        if(list.Count==0)Reply(p,g,"Nothing recoverable at this exact location/time. Inventory recovery needs a pre-change snapshot, item transfer logs and matching final snapshot. Enable LogContainerSnapshots and SnapshotOnSlotChange for future investigations; missing old history cannot be recreated.");
                                    }
                                    if(plan.Area && !plan.SingleBlock)Reply(p,g,"Area mode includes everyone’s logged changes in the selected window. Clear other players from the area; later changes are excluded. Creature replacements may include natural deaths.");
                                    Reply(p,g,$"Full report: {Path.GetFileName(report)}. "+(list.Count==0?"No executable changes; nothing was modified.":$"{(plan.SingleBlock?"Close the container":plan.Area?"Clear area":"Keep target offline")}; backup first. /rollback execute {token} (10-minute expiry)."));
                                    if(list.Count==0){ClearWatchers();changed.Clear();trackedCells.Clear();receipts.Clear();}
                                }).ConfigureAwait(false);
                            }
                            catch(Exception ex) { if(!disposed)await OnGameThreadAsync(()=>{Finish();Reply(p,g,"Preview failed: "+ex.Message,true);}).ConfigureAwait(false); }
                        }
                    }
                    catch(Exception ex){Finish();Reply(p,g,"Preview failed: "+ex.Message,true);}
                }
                Batch(0);
            },"recovery-preview");
        }
        catch(Exception ex){if(!disposed)api.Event.EnqueueMainThreadTask(()=>{Finish();Reply(p,g,cancelRequested?"Preview cancelled.":"Preview failed: "+ex.Message,true);},"recovery-preview-error");}
    }
    private BlockPos? Position(RecoveryCell c)=>int.TryParse(c.Dimension,NumberStyles.Integer,CultureInfo.InvariantCulture,out var d)&&d>=0 ? new BlockPos(c.X,c.Y,c.Z,d) : null;
    private static bool Micro(Block b)=>b.Code?.Path.Contains("chisel",StringComparison.OrdinalIgnoreCase)==true || b.Code?.Path.Contains("microblock",StringComparison.OrdinalIgnoreCase)==true || b.EntityClass?.Contains("Micro",StringComparison.OrdinalIgnoreCase)==true;
    private static bool Multi(Block b)=>b.GetType().Name.Contains("Multiblock",StringComparison.OrdinalIgnoreCase)||b.Code?.Path.Contains("multiblock",StringComparison.OrdinalIgnoreCase)==true;
    private static bool BasicContainer(Block b)=>b.EntityClass!=null && (b.EntityClass.Contains("GenericContainer",StringComparison.OrdinalIgnoreCase)||b.EntityClass.Contains("Chest",StringComparison.OrdinalIgnoreCase)||b.EntityClass.Contains("Basket",StringComparison.OrdinalIgnoreCase)||b.EntityClass.Contains("Trunk",StringComparison.OrdinalIgnoreCase));
    private string? Validate(RecoveryOperation op,out string hash,IServerPlayer? actor=null,bool includeClaims=false)
    {
        hash="";var pos=Position(op.Cell);if(pos==null)return "Unrecognized dimension";
        if (!includeClaims && api.World.Claims?.Get(pos)?.Any() == true) return "Protected claimed land (use --include-claims explicitly)";
        if (actor != null && api.World.Claims != null && api.World.Claims.TestAccess(actor,pos,EnumBlockAccessFlags.BuildOrBreak) != EnumWorldAccessResponse.Granted) return "Current land access denied";
        var ba=api.World.BlockAccessor;
        if(!ba.IsValidPos(pos)||ba.GetChunkAtBlockPos(pos)==null)return "Unloaded/out-of-world location (visit area and preview again)";
        if(op.Kind=="entity-replacement")
        {
            if(pos.dimension!=0)return "Entity replacement supports overworld only";
            if(ba.GetBlock(pos,BlockLayersAccess.Solid).Replaceable<6000)return "Logged entity position is obstructed; restore terrain then preview again";
            if(api.World.GetEntityById(op.OriginalEntityId)!=null)return "Original entity/corpse still loaded";
            var props=api.World.GetEntityType(new AssetLocation(op.EntityCode));if(props==null)return "Missing entity type";
            var candidate=api.World.ClassRegistry.CreateEntity(props);
            if(candidate is not EntityAgent || candidate is EntityPlayer)return "Entity is not a recoverable creature";
            hash="absent:"+op.OriginalEntityId;return null;
        }
        var before=api.World.GetBlock(new AssetLocation(op.Before));var after=api.World.GetBlock(new AssetLocation(op.After));
        if(before==null||after==null)return "Missing block type";
        if(before.ForFluidsLayer||after.ForFluidsLayer||ba.GetBlock(pos,BlockLayersAccess.Fluid).BlockId!=0)return "Fluid state not captured by this logger";
        if(Multi(before)||Multi(after))return "Multiblock structure requires unavailable linked state";
        if(ba.GetBlock(pos,BlockLayersAccess.Solid).BlockId!=after.BlockId)return "Current block differs from recorded final state";
#pragma warning disable CS0618
        if(ba.GetDecors(pos)?.Any(x=>x!=null&&x.BlockId!=0)==true)return "Live decor state would be affected";
#pragma warning restore CS0618
        if(op.Kind=="block" && !string.IsNullOrWhiteSpace(before.EntityClass))
        {
            if(Micro(before))return "Original chisel voxel/material data absent from existing DB";
            if(op.InventoryBefore==null || !BasicContainer(before))return "Original block-entity data unavailable (sign/door/machine/etc.)";
            // Recreating a destroyed static container is partial: snapshot knows inventory, not reinforcement/custom BE metadata.
        }
        var be=ba.GetBlockEntity(pos);
        if(!string.IsNullOrWhiteSpace(after.EntityClass) && be==null)return "Current block entity missing";
        if(op.Kind=="inventory")
        {
            if(be is not IBlockEntityContainer container || container.Inventory==null)return "Live inventory missing";
            if(container.Inventory.Count!=op.SlotCount || !SlotsEqual(container.Inventory,op.InventoryAfter))return "Live container differs from reconstructed final inventory";
        }
        if(op.InventoryBefore!=null)
        {
            foreach(var slot in op.InventoryBefore)
                if(ResolveStack(slot.Code,slot.Count)==null)return "Historical item code missing or invalid stack count";
            // A block chain whose final state is a surviving container must also match its recorded inventory.
            if(op.Kind=="block" && be is IBlockEntityContainer current && (current.Inventory.Count!=op.SlotCount||!SlotsEqual(current.Inventory,op.InventoryAfter)))return "Live container inventory changed";
        }
        hash=LiveHash(pos);return null;
    }
    private static bool SlotsEqual(IInventory inventory,List<RecoverySlot>? saved)
    {
        if(saved==null)return false;var map=saved.ToDictionary(s=>s.Slot);
        for(int i=0;i<inventory.Count;i++)
        {
            var stack=inventory[i]?.Itemstack;
            if(!map.TryGetValue(i,out var expected)){if(stack!=null&&stack.StackSize>0)return false;}
            else if(stack==null || stack.StackSize!=expected.Count || RollbackRecoveryPlanner.Canonical(stack.Collectible.Code.ToShortString())!=RollbackRecoveryPlanner.Canonical(expected.Code))return false;
        }
        return true;
    }
    private ItemStack? ResolveStack(string code,int count)
    {
        if(count<=0)return null;var location=new AssetLocation(code);var item=api.World.GetItem(location);var block=api.World.GetBlock(location);
        if(item!=null && block!=null && block.BlockId!=0)return null;
        ItemStack? stack=item!=null?new ItemStack(item,count):block!=null&&block.BlockId!=0?new ItemStack(block,count):null;
        return stack!=null&&count<=stack.Collectible.MaxStackSize?stack:null;
    }
    private string LiveHash(BlockPos pos)
    {
        var tree=new TreeAttribute();var be=api.World.BlockAccessor.GetBlockEntity(pos);be?.ToTreeAttributes(tree);
        using var ms=new MemoryStream();using(var writer=new BinaryWriter(ms,Encoding.UTF8,true))tree.ToBytes(writer);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }
    private object Capture(BlockPos pos)
    {
        var tree=new TreeAttribute();api.World.BlockAccessor.GetBlockEntity(pos)?.ToTreeAttributes(tree);
        using var ms=new MemoryStream();using(var writer=new BinaryWriter(ms,Encoding.UTF8,true))tree.ToBytes(writer);
        return new {block=api.World.BlockAccessor.GetBlock(pos,BlockLayersAccess.Solid).Code.ToShortString(),blockEntityBase64=Convert.ToBase64String(ms.ToArray())};
    }
    private void RestoreSlots(IInventory inv,List<RecoverySlot> desired)
    {
        var map=desired.ToDictionary(x=>x.Slot);var stacks=new ItemStack?[inv.Count];
        // Prepare all stacks before touching the inventory, preserving current attributes where the item code survives.
        for(int i=0;i<inv.Count;i++)if(map.TryGetValue(i,out var slot))
        {
            var existing=inv[i]?.Itemstack;
            if(existing!=null&&RollbackRecoveryPlanner.Canonical(existing.Collectible.Code.ToShortString())==RollbackRecoveryPlanner.Canonical(slot.Code))
            {stacks[i]=existing.Clone();stacks[i]!.StackSize=slot.Count;}
            else stacks[i]=ResolveStack(slot.Code,slot.Count)??throw new InvalidOperationException("Missing historical item type.");
        }
        for(int i=0;i<inv.Count;i++)if(inv[i]==null)throw new InvalidOperationException("Inventory slot is unavailable; rollback was not applied.");
        for(int i=0;i<inv.Count;i++){var target=inv[i]!;target.Itemstack=stacks[i];target.MarkDirty();}
    }
    private async Task InitializeReceiptsAsync()
    {
        try
        {
            await receiptIndex.InitializeAsync(lifetime.Token).ConfigureAwait(false);
            await OnGameThreadAsync(() => receiptReady = true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (disposed) return;
            api.Event.EnqueueMainThreadTask(() =>
            {
                receiptFailure = "Recovery disabled: receipt journal/index could not be read. Preserve the journal and check the server error log.";
                api.Logger.Error("[Recovery] Receipt initialization failed: {0}", ex.Message);
            }, "recovery-receipt-index-failed");
        }
    }
    private Task OnGameThreadAsync(Action action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration=lifetime.Token.Register(()=>completion.TrySetCanceled(lifetime.Token));
        api.Event.EnqueueMainThreadTask(()=>
        {
            if(!completion.Task.IsCompleted && !disposed)
            {
                try {action();completion.TrySetResult();}
                catch(Exception ex){completion.TrySetException(ex);}
            }
            registration.Dispose();
        },"recovery-step");
        return completion.Task;
    }
    private async Task Execute(IServerPlayer p,int g,Plan plan)
    {
        // Capture immutable identity while entering from the game-thread command.
        string administratorUid=p.PlayerUID,administratorName=p.PlayerName;
        int blocks=0,inventories=0,entities=0,skipped=0,index=0;
        string audit=storage.DatabasePath+".recovery-"+plan.Token+".jsonl";
        try
        {
            await using var journal=new RecoveryJournal(audit,receiptPath);
            var cancellation=operationCancellation!.Token;
            var history=await storage.QueryRollbackRecoveryAsync(plan.Uid,plan.From,plan.To,plan.Area?plan.Center:null,plan.Area?plan.Radius:0,cancellation).ConfigureAwait(false);
            var fresh=(await Task.Run(()=>RollbackRecoveryPlanner.Build(history,plan.From,plan.To,plan.Inventories,plan.Entities,cancellation),cancellation).ConfigureAwait(false)).Operations.ToDictionary(x=>x.ReceiptKey);
            foreach(var prepared in plan.Changes)
            {
                index++;var op=prepared.Operation;string? reason=null;object? current=null;
                await OnGameThreadAsync(()=>
                {
                    if(cancelRequested)throw new OperationCanceledException();
                    CheckOccupancy();
                    if(receipts.Contains(op.ReceiptKey)||changed.Contains(op.Cell.Key)||!fresh.TryGetValue(op.ReceiptKey,out var updated)||updated.Fingerprint!=op.Fingerprint)
                        reason="Receipt, live change or changed historical plan";
                    else { reason=Validate(op,out var hash,p,plan.IncludeClaims);if(reason==null && hash!=prepared.LiveHash)reason="Live block-entity state changed since preview"; }
                    if(reason==null && op.Kind!="entity-replacement")current=Capture(Position(op.Cell)!);
                }).ConfigureAwait(false);
                if(reason!=null)
                {
                    skipped++;
                    await journal.AppendAsync(new {phase="skipped",operation=op,reason,utc=DateTime.UtcNow}).ConfigureAwait(false);
                    if(index%256==0)await OnGameThreadAsync(()=>Reply(p,g,$"Recovery progress: {index}/{plan.Changes.Count}, skipped={skipped}.")).ConfigureAwait(false);
                    continue;
                }
                await journal.AppendAsync(
                    new {phase="intent",utc=DateTime.UtcNow,admin=administratorUid,mode=plan.Mode,target=plan.Uid,center=plan.Center,radius=plan.Radius,from=plan.From,to=plan.To,operation=op,current},
                    new {key=op.ReceiptKey,phase="intent",token=plan.Token,utc=DateTime.UtcNow}).ConfigureAwait(false);
                object? applied=null;
                await OnGameThreadAsync(()=>
                {
                    // The receipt is durable before mutation; retain it on cancellation,
                    // changes while flushing, partial failure or uncertain crash outcome.
                    receipts.Add(op.ReceiptKey);
                    if(cancelRequested)throw new OperationCanceledException();
                    CheckOccupancy();
                    reason=Validate(op,out var hash,p,plan.IncludeClaims);
                    if(changed.Contains(op.Cell.Key)||reason!=null||hash!=prepared.LiveHash)
                    {reason??="Live state changed while writing durable intent";return;}
                    var pos=Position(op.Cell)!;
                    if(op.Kind=="entity-replacement")
                    {
                        var type=api.World.GetEntityType(new AssetLocation(op.EntityCode));var entity=api.World.ClassRegistry.CreateEntity(type);
                        if(!EntityPositionResolver.TrySetPose(entity,pos.X+0.5,pos.Y,pos.Z+0.5,0,0))throw new InvalidOperationException("Entity pose setter unavailable.");
                        api.World.SpawnEntity(entity);entities++;
                        applied=new {phase="applied",operation=op,createdEntityId=entity.EntityId};
                    }
                    else
                    {
                        if(op.Kind=="block")
                        {api.World.BlockAccessor.SetBlock(api.World.GetBlock(new AssetLocation(op.Before))!.BlockId,pos,BlockLayersAccess.Solid);blocks++;}
                        if(op.InventoryBefore!=null)
                        {
                            if(api.World.BlockAccessor.GetBlockEntity(pos) is not IBlockEntityContainer container || container.Inventory.Count!=op.SlotCount)
                                throw new InvalidOperationException("Container initialization/slot layout differs; block may have been restored but inventory was not.");
                            RestoreSlots(container.Inventory,op.InventoryBefore);inventories++;
                            api.World.BlockAccessor.GetBlockEntity(pos)?.MarkDirty(true);
                        }
                        api.World.BlockAccessor.MarkBlockDirty(pos);dirty?.Invoke(pos.dimension,pos.X,pos.Z);
                        if(!storage.TryEnqueue(new LogEventRecord {TimestampUtc=DateTime.UtcNow,Action=LogActionType.BlockUse,PlayerUid=administratorUid,PlayerName=administratorName,WorldId=api.World.SavegameIdentifier??"unknownworld",Dimension=op.Cell.Dimension,X=pos.X,Y=pos.Y,Z=pos.Z,BlockCode=op.Before,Meta=new(){["rollbackToken"]=plan.Token,["oldBlockCode"]=op.After,["newBlockCode"]=op.Before}}))throw new InvalidOperationException("Audit queue rejected recovery barrier; stopped.");
                        applied=new {phase="applied",operation=op};
                    }
                }).ConfigureAwait(false);
                if(applied==null)
                {skipped++;await journal.AppendAsync(new {phase="skipped",operation=op,reason,utc=DateTime.UtcNow}).ConfigureAwait(false);}
                else await journal.AppendAsync(applied,new {key=op.ReceiptKey,phase="applied",token=plan.Token,utc=DateTime.UtcNow}).ConfigureAwait(false);
                if(index%256==0)await OnGameThreadAsync(()=>Reply(p,g,$"Recovery progress: {index}/{plan.Changes.Count}, skipped={skipped}.")).ConfigureAwait(false);
                // One operation per dispatch; yield between mutations instead of doing
                // up to 16 operations and 64 fsyncs in one game-thread callback.
                await Task.Delay(10,cancellation).ConfigureAwait(false);
            }
            await journal.AppendAsync(new {phase="complete",utc=DateTime.UtcNow,blocks,inventories,entities,skipped}).ConfigureAwait(false);
            // Release receipt handles before advertising that another recovery may start.
            await journal.DisposeAsync().ConfigureAwait(false);
            await OnGameThreadAsync(()=>{Reply(p,g,$"Recovery complete: {blocks} block locations, {inventories} inventories, {entities} replacement creatures; {skipped} conflicts/changes skipped. Journal: {Path.GetFileName(audit)}");Finish();}).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            try
            {
                await using var failureJournal=new RecoveryJournal(audit,receiptPath);
                await failureJournal.AppendAsync(new {phase=cancelRequested||disposed?"cancelled":"stopped",utc=DateTime.UtcNow,blocks,inventories,entities,skipped,error=ex.Message}).ConfigureAwait(false);
            }
            catch { /* Original I/O failure may prevent even a terminal record; receipts remain conservative. */ }
            if(!disposed)await OnGameThreadAsync(()=>
            {
                Reply(p,g,$"Recovery {(cancelRequested?"cancelled":"stopped")}: {blocks} blocks, {inventories} inventories, {entities} creatures applied. {ex.Message} Inspect journal; uncertain receipts prevent repeat duplication.",true);
                if(!cancelRequested)api.Logger.Error("[Recovery] {0}",ex);
                Finish();
            }).ConfigureAwait(false);
        }
        void CheckOccupancy()
        {if(Occupied(plan,administratorUid))throw new InvalidOperationException(plan.Area?"Another player entered the selected area; stopped.":"Target came online; stopped.");}
    }
    private void Finish(){busy=false;activeAdmin="";plans.Clear();receipts.Clear();ClearWatchers();changed.Clear();trackedCells.Clear();trackAllDuringRead=false;operationCancellation?.Dispose();operationCancellation=null;}
    public void Dispose(){disposed=true;if(maintenanceTick!=0)api.Event.UnregisterGameTickListener(maintenanceTick);operationCancellation?.Cancel();lifetime.Cancel();Finish();api.Event.DidBreakBlock-=Break;api.Event.DidPlaceBlock-=Place;api.Event.DidUseBlock-=Use;}
}
