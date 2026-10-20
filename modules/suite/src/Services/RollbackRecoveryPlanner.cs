using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BlockLogger.Models;

namespace BlockLogger.Services;

public sealed class RollbackHistory
{
    public bool AreaMode { get; set; }
    public string PlayerUid { get; set; } = "";
    public string WorldId { get; set; } = "";
    public List<LogEventRecord> Events { get; } = new();
    public List<ContainerSnapshotRecord> Snapshots { get; } = new();
}
public sealed record RecoveryCell(string Dimension,int X,int Y,int Z)
{
    public string Key => $"{Dimension}:{X}:{Y}:{Z}";
    public static RecoveryCell At(LogEventRecord r) => new(Normalize(r.Dimension),r.X,r.Y,r.Z);
    public static RecoveryCell At(ContainerSnapshotRecord r) => new(Normalize(r.Dimension),r.X,r.Y,r.Z);
    public static string Normalize(string d) => d.Equals("overworld",StringComparison.OrdinalIgnoreCase) ? "0" : d;
}
public sealed record RecoverySlot(int Slot,string Code,int Count);
public sealed record RecoveryLiveInventory(RecoveryCell Cell,string BlockCode,string InventoryId,int SlotCount,List<RecoverySlot> Slots);
public sealed record RecoveryMissingBlock(RecoveryCell Cell,int BlockId);
public sealed class RecoveryOperation
{
    public bool PreserveBlockEntity { get; set; }
    public int? MissingBlockId { get; set; }
    public string Evidence { get; set; } = "";
    public string Kind { get; set; } = "block";
    public RecoveryCell Cell { get; set; } = new("0",0,0,0);
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public List<RecoverySlot>? InventoryBefore { get; set; }
    public List<RecoverySlot>? InventoryAfter { get; set; }
    public int SlotCount { get; set; }
    public long OriginalEntityId { get; set; }
    public string EntityCode { get; set; } = "";
    public string ReceiptKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public List<long> SourceIds { get; set; } = new();
}
public sealed class RecoveryBuild
{
    public List<RecoveryOperation> Operations { get; } = new();
    public Dictionary<string,int> Reasons { get; } = new();
    public void Skip(string reason) => Reasons[reason]=Reasons.GetValueOrDefault(reason)+1;
}
public static class RollbackRecoveryPlanner
{
    private static bool Mutation(LogEventRecord r) => r.Action is LogActionType.BlockBreak or LogActionType.BlockPlace or LogActionType.ContainerTake or LogActionType.ContainerPut;
    private static bool BlockMutation(LogEventRecord r) => r.Action is LogActionType.BlockBreak or LogActionType.BlockPlace;
    public static RecoveryBuild Build(RollbackHistory history,DateTime from,DateTime to,bool inventories,bool entities,CancellationToken cancellationToken=default,RecoveryLiveInventory? liveInventory=null,RecoveryMissingBlock? missingBlock=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result=new RecoveryBuild();
        var snapshotGroups=history.Snapshots.GroupBy(s=>{cancellationToken.ThrowIfCancellationRequested();return RecoveryCell.At(s);}).ToDictionary(g=>g.Key,g=>g.OrderByDescending(s=>s.TimestampUtc).ThenByDescending(s=>s.StorageId).ToList());
        foreach(var group in history.Events.Where(r=>{cancellationToken.ThrowIfCancellationRequested();return (int)r.Action<=6;}).GroupBy(RecoveryCell.At))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows=group.OrderByDescending(r=>r.TimestampUtc).ThenByDescending(r=>r.StorageId).ToList();
            var target=rows.Where(r=>(history.AreaMode || r.PlayerUid==history.PlayerUid) && r.TimestampUtc>=from && r.TimestampUtc<=to && Mutation(r)).ToList();
            if(target.Count==0) continue;
            var earliest=target.Min(r=>r.TimestampUtc);
            var earliestId=target.Where(r=>r.TimestampUtc==earliest).Min(r=>r.StorageId);
            var later=rows.Where(r=>r.TimestampUtc>earliest || r.TimestampUtc==earliest && r.StorageId>=earliestId).ToList();
            if(later.Any(r=>r.Meta.ContainsKey("rollbackToken") || (!history.AreaMode && r.PlayerUid!=history.PlayerUid) || r.TimestampUtc>to))
            { result.Skip("Later activity/other actor/previous rollback at location"); continue; }
            snapshotGroups.TryGetValue(group.Key,out var snapshots);
            snapshots ??=new();
            // Snapshots attributed to someone else after the attack indicate shared/automated activity.
            if(snapshots.Any(s=>s.TimestampUtc>=earliest && (history.AreaMode ? s.TimestampUtc>to : s.PlayerUid!=history.PlayerUid)))
            { result.Skip("Later/shared container snapshot"); continue; }
            var blocks=target.Where(BlockMutation).ToList();
            string before="",after=""; bool valid=true;
            foreach(var r in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var old=r.Meta.GetValueOrDefault("oldBlockCode");
                var next=r.Meta.GetValueOrDefault("newBlockCode");
                // Older break records still identify the removed block. Never infer a placed block's overwritten state.
                if(string.IsNullOrWhiteSpace(old) && r.Action==LogActionType.BlockBreak) old=r.BlockCode;
                if(string.IsNullOrWhiteSpace(next)) next=r.Action==LogActionType.BlockBreak ? "air" : r.BlockCode;
                if(string.IsNullOrWhiteSpace(old) || string.IsNullOrWhiteSpace(next) || old=="unknown" || next=="unknown") {valid=false;break;}
                if(after.Length==0) after=next;
                else if(Canonical(before)!=Canonical(next)) {valid=false;break;}
                before=old;
            }
            if(!valid) { result.Skip("Missing old block code or discontinuous block history"); continue; }
            var deltas=target.Where(r=>r.Action is LogActionType.ContainerTake or LogActionType.ContainerPut).ToList();
            RecoveryOperation? operation=null;
            if(blocks.Count>0 && Canonical(before)!=Canonical(after)) operation=new() {Kind="block",Cell=group.Key,Before=before,After=after};
            if(inventories && (deltas.Count>0 || blocks.Count>0))
            {
                var first=target.Min(r=>r.TimestampUtc);
                var baseline=snapshots.FirstOrDefault(s=>s.TimestampUtc<first && (before.Length==0 || Canonical(s.BlockCode)==Canonical(before)));
                if(baseline!=null && ValidSnapshot(baseline))
                {
                    var allDeltas=rows.Where(r=>r.TimestampUtc>baseline.TimestampUtc && r.TimestampUtc<=to && (r.Action is LogActionType.ContainerTake or LogActionType.ContainerPut))
                        .OrderBy(r=>r.TimestampUtc).ThenBy(r=>r.StorageId).ToList();
                    var pre=ReadSlots(baseline); var final=ReadSlots(baseline);
                    bool complete=true;
                    if(!string.IsNullOrWhiteSpace(baseline.InventoryId) && allDeltas.Any(r=>r.Meta.TryGetValue("inventoryId",out var id) && !string.IsNullOrWhiteSpace(id) && id!=baseline.InventoryId)) complete=false;
                    foreach(var r in allDeltas)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if(r.TimestampUtc<earliest || r.TimestampUtc==earliest && r.StorageId<earliestId)
                            complete &= ApplyDelta(pre,r,baseline.SlotCount);
                        complete &= ApplyDelta(final,r,baseline.SlotCount);
                    }
                    // A baseline across any prior block replacement is not the same inventory.
                    if(rows.Any(r=>BlockMutation(r) && r.TimestampUtc>baseline.TimestampUtc && (r.TimestampUtc<earliest || r.TimestampUtc==earliest && r.StorageId<earliestId))) complete=false;
                    var latest=snapshots.FirstOrDefault(s=>s.TimestampUtc>=first);
                    if(blocks.Count==0 || before.Length>0 && Canonical(before)==Canonical(after))
                    {
                        var lastDelta=target.Max(r=>r.TimestampUtc);
                        if(latest==null || latest.TimestampUtc<=lastDelta || !ValidSnapshot(latest) || Canonical(latest.BlockCode)!=Canonical(baseline.BlockCode) || !string.IsNullOrWhiteSpace(baseline.InventoryId) && latest.InventoryId!=baseline.InventoryId || latest.SlotCount!=baseline.SlotCount || !Equal(final,ReadSlots(latest))) complete=false;
                    }
                    if(complete)
                    {
                        if(operation==null && (deltas.Count>0 || blocks.Count>0 && before.Length>0 && Canonical(before)==Canonical(after)))
                            operation=new() {Kind="inventory",Cell=group.Key,Before=baseline.BlockCode,After=baseline.BlockCode};
                        if(operation!=null)
                        {
                            operation.InventoryBefore=Sorted(pre); operation.InventoryAfter=Sorted(final); operation.SlotCount=baseline.SlotCount;
                        }
                    }
                    else result.Skip("Inventory deltas/snapshots disagree or are incomplete");
                }
                else if(deltas.Count>0) result.Skip("No valid pre-change container snapshot");
            }
            else if(deltas.Count>0) result.Skip("Inventory recovery requires --inventories (saved codes/counts only)");
            if(operation==null) continue;
            operation.SourceIds=target.Select(r=>r.StorageId).OrderBy(x=>x).ToList();
            Stamp(operation,history.WorldId);
            result.Operations.Add(operation);
        }
        if(liveInventory!=null && !result.Operations.Any(o=>o.Cell==liveInventory.Cell))
            AddLiveInventory(result,history,from,to,liveInventory,cancellationToken);
        if(missingBlock!=null)AddMissingBlock(result,history,from,to,missingBlock);
        var deaths=history.Events.Where(r=>r.Action==LogActionType.EntityDeath && r.TimestampUtc>=from && r.TimestampUtc<=to &&
            (history.AreaMode || r.PlayerUid==history.PlayerUid || r.Meta.GetValueOrDefault("sourcePlayerUid")==history.PlayerUid));
        var seen=new HashSet<long>();
        foreach(var r in deaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!entities) { result.Skip("Entity death: replacement requires --replace-entities"); continue; }
            if(!long.TryParse(r.Meta.GetValueOrDefault("entityId"),out var id) || id<=0 || !seen.Add(id)) {result.Skip("Missing/duplicate original entity ID");continue;}
            var code=r.Meta.GetValueOrDefault("entityCode"); var type=r.Meta.GetValueOrDefault("entityClass") ?? "";
            if(string.IsNullOrWhiteSpace(code) || type.Contains("Player",StringComparison.OrdinalIgnoreCase) || Canonical(code)=="game:player")
            { result.Skip("Player or unknown entity cannot be replaced"); continue; }
            var op=new RecoveryOperation {Kind="entity-replacement",Cell=RecoveryCell.At(r),EntityCode=code,OriginalEntityId=id,SourceIds=new(){r.StorageId}};
            Stamp(op,history.WorldId);result.Operations.Add(op);
        }
        return result;
    }
    private static void AddLiveInventory(RecoveryBuild build,RollbackHistory history,DateTime from,DateTime to,RecoveryLiveInventory live,CancellationToken token)
    {
        if(!history.AreaMode)return;
        var rows=history.Events.Where(r=>RecoveryCell.At(r)==live.Cell&&r.TimestampUtc>=from).OrderByDescending(r=>r.TimestampUtc).ThenByDescending(r=>r.StorageId).ToList();
        var deltas=rows.Where(r=>r.TimestampUtc<=to&&r.Action is LogActionType.ContainerTake or LogActionType.ContainerPut).ToList();
        if(deltas.Count==0){build.Skip("No take/put rows at the selected location/time");return;}
        if(rows.Any(r=>r.TimestampUtc>to||BlockMutation(r)||r.Meta.ContainsKey("rollbackToken"))){build.Skip("Live-anchor recovery refused later activity, block replacement or rollback barrier");return;}
        if(history.Snapshots.Any(x=>RecoveryCell.At(x)==live.Cell&&x.TimestampUtc<deltas.Min(r=>r.TimestampUtc))){build.Skip("Recorded baseline exists; resolve snapshot conflicts instead of bypassing them");return;}
        if(string.IsNullOrWhiteSpace(live.InventoryId)||deltas.Any(r=>r.Meta.GetValueOrDefault("inventoryId")!=live.InventoryId||Canonical(r.BlockCode)!=Canonical(live.BlockCode))){build.Skip("Take/put inventory identity or block code is missing/different");return;}
        var current=live.Slots.ToDictionary(x=>x.Slot);var before=new Dictionary<int,RecoverySlot>(current);
        foreach(var row in deltas){token.ThrowIfCancellationRequested();var inverse=new LogEventRecord{Action=row.Action==LogActionType.ContainerTake?LogActionType.ContainerPut:LogActionType.ContainerTake,Meta=row.Meta};if(!ApplyDelta(before,inverse,live.SlotCount)){build.Skip("Take/put rows cannot be reversed consistently from the current contents");return;}}
        if(Equal(before,current)){build.Skip("Take/put rows produce no net inventory change");return;}
        var op=new RecoveryOperation{Kind="inventory",Cell=live.Cell,Before=live.BlockCode,After=live.BlockCode,SlotCount=live.SlotCount,InventoryBefore=Sorted(before),InventoryAfter=Sorted(current),SourceIds=deltas.Select(r=>r.StorageId).OrderBy(x=>x).ToList(),Evidence="Explicit --from-live: reversed recorded transfers from captured current contents; unlogged transfers and old attributes cannot be verified."};
        Stamp(op,history.WorldId);build.Operations.Add(op);
    }
    private static void AddMissingBlock(RecoveryBuild build,RollbackHistory history,DateTime from,DateTime to,RecoveryMissingBlock missing)
    {
        if(!history.AreaMode)return;
        var existing=build.Operations.FirstOrDefault(o=>o.Cell==missing.Cell&&o.Kind=="block");
        if(existing!=null){existing.MissingBlockId=missing.BlockId;existing.Evidence="Recorded block-change rollback; current missing numeric ID is bound to this preview.";return;}
        var rows=history.Events.Where(r=>RecoveryCell.At(r)==missing.Cell).ToList();
        if(rows.Any(r=>r.TimestampUtc>to||r.Meta.ContainsKey("rollbackToken"))){build.Skip("Missing-block history has later activity or a rollback barrier");return;}
        // A remapper does not emit ordinary break/place events. Only an unambiguous
        // recorded final identity can reconnect a placeholder; never infer a mod asset.
        var evidence=rows.Where(r=>r.TimestampUtc<=to&&r.TimestampUtc>=from&&r.Action is LogActionType.BlockUse or LogActionType.ContainerOpen or LogActionType.ContainerTake or LogActionType.ContainerPut).ToList();
        var snaps=history.Snapshots.Where(x=>RecoveryCell.At(x)==missing.Cell&&x.TimestampUtc<=to).OrderByDescending(x=>x.TimestampUtc).ToList();
        var codes=evidence.Select(r=>r.BlockCode).Concat(snaps.Take(1).Select(x=>x.BlockCode)).Where(c=>!string.IsNullOrWhiteSpace(c)&&c!="unknown").Select(Canonical).Distinct().ToArray();
        if(codes.Length!=1||rows.Any(BlockMutation)){build.Skip("No unambiguous logged block identity for this missing ID; configure Block ID Migration");return;}
        var op=new RecoveryOperation{Cell=missing.Cell,Before=codes[0],After=codes[0],MissingBlockId=missing.BlockId,Evidence="Unlogged remap: one recorded block identity at this exact location. Preview restores only this missing block; it does not migrate the world.",SourceIds=evidence.Select(r=>r.StorageId).OrderBy(x=>x).ToList()};
        // Snapshot-only evidence must bind receipts even without event IDs.
        if(op.SourceIds.Count==0&&snaps.Count>0)op.SourceIds.Add(-snaps[0].StorageId);
        Stamp(op,history.WorldId);build.Operations.RemoveAll(o=>o.Cell==missing.Cell);build.Operations.Add(op);
    }
    public static string Canonical(string code) => code.Contains(':') ? code : "game:"+code;
    private static void Stamp(RecoveryOperation op,string world)
    {
        string sources=string.Join(",",op.SourceIds);
        op.ReceiptKey=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(world+":"+op.Kind+":"+(op.Kind=="entity-replacement" ? op.OriginalEntityId.ToString(CultureInfo.InvariantCulture) : op.Cell.Key+":"+sources))));
        op.Fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(op.ReceiptKey+":"+Canonical(op.Before)+":"+Canonical(op.After)+":"+op.SlotCount+":"+
            string.Join(";",(op.InventoryBefore??new()).Select(s=>$"{s.Slot}:{Canonical(s.Code)}:{s.Count}"))+":"+
            string.Join(";",(op.InventoryAfter??new()).Select(s=>$"{s.Slot}:{Canonical(s.Code)}:{s.Count}"))+":"+op.EntityCode+":"+op.OriginalEntityId)));
    }
    private static bool ValidSnapshot(ContainerSnapshotRecord s) => s.SlotCount>0 && s.SlotCount<=4096 && s.Slots.All(x=>x.SlotId>=0 && x.SlotId<s.SlotCount && x.StackSize>0 && !string.IsNullOrWhiteSpace(x.ItemCode)) && s.Slots.Select(x=>x.SlotId).Distinct().Count()==s.Slots.Count && s.Slots.Count==s.NonEmptySlotCount && s.Slots.Sum(x=>(long)x.StackSize)==s.TotalItemCount;
    private static Dictionary<int,RecoverySlot> ReadSlots(ContainerSnapshotRecord s) => s.Slots.ToDictionary(x=>x.SlotId,x=>new RecoverySlot(x.SlotId,Canonical(x.ItemCode),x.StackSize));
    private static List<RecoverySlot> Sorted(Dictionary<int,RecoverySlot> s) => s.Values.OrderBy(x=>x.Slot).ToList();
    private static bool Equal(Dictionary<int,RecoverySlot> a,Dictionary<int,RecoverySlot> b) => a.Count==b.Count && a.All(x=>b.TryGetValue(x.Key,out var slot) && slot==x.Value);
    private static bool ApplyDelta(Dictionary<int,RecoverySlot> slots,LogEventRecord r,int count)
    {
        if(!int.TryParse(r.Meta.GetValueOrDefault("slotId"),out var id) || id<0 || id>=count || !int.TryParse(r.Meta.GetValueOrDefault("quantity"),out var quantity) || quantity<=0) return false;
        var code=r.Meta.GetValueOrDefault("itemCode"); if(string.IsNullOrWhiteSpace(code))return false;code=Canonical(code);
        slots.TryGetValue(id,out var slot);
        if(slot!=null && slot.Code!=code) return false;
        long n=(slot?.Count??0)+(r.Action==LogActionType.ContainerTake ? -(long)quantity : quantity);
        if(n<0 || n>int.MaxValue)return false;
        if(n==0)slots.Remove(id);else slots[id]=new(id,code,(int)n);
        return true;
    }
}
