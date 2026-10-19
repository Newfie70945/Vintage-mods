using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private string InventoryManifest(Plan p)=>Path.Combine(directory,"migration-inventories-"+p.Id+".db");
    private string InventoryReceipts=>Path.Combine(directory,"migration-inventory-receipts.db");
    private static string ManifestIdentity(Plan p)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{p.Id,p.World,p.Registry,p.InventoryRegistry,p.Inventory,Rules=EffectiveMappings(p).Select(m=>new{m.Source,m.Target,m.TargetCode})}))));
    private static MigrationInventoryReport CopyReport(MigrationInventoryReport r)=>new(){State=r.State,Message=r.Message,Chunks=r.Chunks,Prepared=r.Prepared,Preserved=r.Preserved,Recovered=r.Recovered,Items=r.Items,Skipped=r.Skipped,Samples=r.Samples.Take(50).ToList()};
    private void ValidateInventory(Plan p,bool ready)
    {
        p.Inventory.Validate();if(p.Inventory.Mode=="preserve")return;
        if(p.InventoryRegistry!=RestoreRegistry())throw new InvalidOperationException("Item/block mapping changed; review inventory recovery again.");
        if(ready)
        {
            if(p.InventoryReport.State!="complete")throw new InvalidOperationException("Wait for inventory preparation to finish successfully before enabling migration.");
            string path=InventoryManifest(p);if(!File.Exists(path)||new FileInfo(path).Length>256*1024*1024)throw new InvalidDataException("Prepared inventories are missing or exceed the supported size.");
            using var file=File.OpenRead(path);if(Convert.ToHexString(SHA256.HashData(file))!=p.InventoryManifestHash)throw new InvalidDataException("Prepared inventory file changed after review.");
            MigrationInventoryStore.Verify(path,ManifestIdentity(p));
        }
    }
    private void StartMigrationInventoryPreparation(Plan p)
    {
        p.Inventory.Validate();if(p.Inventory.Mode=="preserve")return;
        if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for the current scan to finish before preparing inventory recovery.");
        p.InventoryRegistry=RestoreRegistry();var catalog=CaptureRestoreCatalog();string worldPath=Database().DatabaseFilename,history=historyDatabasePath??"",manifest=InventoryManifest(p);
        var server=(ServerMain)api.World;var mappings=EffectiveMappings(p).ToDictionary(m=>m.Source);var report=new MigrationInventoryReport{State="running",Message="Checking saved container metadata and exact-location BlockLogger snapshots. No world changes are made."};p.InventoryReport=CopyReport(report);
        cancel?.Dispose();cancel=new();var token=cancel.Token;DateTime cutoff=p.Inventory.BeforeUtc??DateTime.UtcNow;
        foreach(string old in Directory.EnumerateFiles(directory,"migration-inventories-*.db").Take(1024))
            if(old!=manifest&&old!=loadedManifest&&(loadedMigration.Plan==null||old!=InventoryManifest(loadedMigration.Plan)))File.Delete(old);
        MigrationInventoryStore.EnsureReceipts(InventoryReceipts);worker=Task.Run(()=>
        {
            var pool=new ChunkDataPool(32,server){CacheSize=32};var codec=new BlockIdChunkCodec(pool);long? cursor=null;long bytesPrepared=0;
            try
            {
                foreach(string suffix in new[]{"","-journal","-wal","-shm"})if(File.Exists(manifest+suffix))File.Delete(manifest+suffix);MigrationInventoryStore.Create(manifest,ManifestIdentity(p));using var output=MigrationInventoryStore.Connect(manifest,false);using var receipts=MigrationInventoryStore.Connect(InventoryReceipts,true);
                while(true)
                {
                    token.ThrowIfCancellationRequested();var page=Page(worldPath,cursor);if(page.Count==0)break;
                    using var tx=output.BeginTransaction();
                    foreach(var row in page)
                    {
                        token.ThrowIfCancellationRequested();cursor=row.Key;report.Chunks++;
                        try
                        {
                            var counts=codec.Inspect(row.Data,mappings.ContainsKey);var candidates=counts.Where(e=>catalog.Blocks.TryGetValue(mappings[e.Key].TargetCode,out var b)&&b.Supported).ToDictionary(e=>e.Key,e=>e.Value);
                            if(candidates.Count==0)continue;
                            var fields=BlockIdChunkCodec.Fields(row.Data).Where(f=>f.Tag==8).ToArray();if(fields.Length>256||fields.Sum(f=>(long)f.Length)>8*1024*1024)throw new InvalidDataException("Retained container records exceed verification bounds.");
                            var records=fields.Select(f=>f.Wire==2?codec.EntityAtBytes(row.Data,f):throw new InvalidDataException("Invalid saved container record.")).ToList();
                            var chunk=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));var cells=codec.ReadCells(row.Data,records.Select(e=>(((e.Summary.InternalY!.Value&31)*32+(e.Summary.Z!.Value&31))*32)+(e.Summary.X!.Value&31)));
                            var hosted=new Dictionary<int,long>();var unique=new HashSet<(int,int,int,int)>();
                            foreach(var entity in records)
                            {
                                token.ThrowIfCancellationRequested();var saved=entity.Summary;int index=(((saved.InternalY!.Value&31)*32+(saved.Z!.Value&31))*32)+(saved.X!.Value&31);int source=cells[index][0];
                                if(!candidates.ContainsKey(source))continue;hosted[source]=hosted.GetValueOrDefault(source)+1;
                                string location=$"{saved.X},{saved.Y},{saved.Z} dim {saved.Dimension}";
                                try
                                {
                                    if(saved.X/32!=chunk.X||saved.Y/32!=chunk.Y||saved.Z/32!=chunk.Z||saved.Dimension!=chunk.Dimension||!unique.Add((saved.X!.Value,saved.Y!.Value,saved.Z!.Value,saved.Dimension!.Value)))throw new InvalidDataException("Duplicate or out-of-chunk retained container.");
                                    var mapping=mappings[source];var entry=PrepareMigrationInventoryEntry(p,row.Key,source,mapping,catalog,entity,history,cutoff);
                                    MigrationInventoryStore.CheckReceipt(receipts,null,p.World,entry);
                                    if(report.Prepared>=100000||(bytesPrepared+=entry.Payload.Length)>240*1024*1024)throw new IOException("Recovery exceeds the 100,000-container / 240 MiB preparation limit. Use a focused source-ID scan.");
                                    MigrationInventoryStore.Add(output,entry,tx);report.Prepared++;
                                    if(entry.SnapshotHash.Length>0){report.Recovered++;report.Items+=entry.Items;}else report.Preserved++;
                                }
                                catch(Exception e)when(e is InvalidOperationException or InvalidDataException or SqliteException or ArgumentException)
                                {report.Skipped++;if(report.Samples.Count<50)report.Samples.Add(location+": "+e.Message);}
                            }
                            foreach(var c in candidates)if(c.Value[0]>hosted.GetValueOrDefault(c.Key))
                            {report.Skipped+=c.Value[0]-hosted.GetValueOrDefault(c.Key);if(report.Samples.Count<50)report.Samples.Add($"Chunk {row.Key}, ID {c.Key}: missing retained container metadata; shape/material and inventories cannot be invented.");}
                        }
                        catch(Exception e)when(e is InvalidDataException or InvalidOperationException or NotSupportedException or ArgumentException)
                        {report.Skipped++;if(report.Samples.Count<50)report.Samples.Add("Chunk "+row.Key+": "+e.Message);}
                    }
                    tx.Commit();lock(gate)p.InventoryReport=CopyReport(report);Thread.Sleep(2);
                }
                using(var check=output.CreateCommand()){check.CommandText="PRAGMA quick_check";if(Convert.ToString(check.ExecuteScalar())!="ok")throw new InvalidDataException("Inventory preparation failed integrity verification.");}
                using(var file=File.OpenRead(manifest))p.InventoryManifestHash=Convert.ToHexString(SHA256.HashData(file));
                report.State="complete";report.Message=$"Reviewed {report.Prepared} compatible container(s): {report.Recovered} inventory snapshot recoveries ({report.Items} items), {report.Preserved} retained inventories preserved, {report.Skipped} skipped candidates. Snapshots restore logged codes/quantities with default attributes; written text, durability and custom contents were not logged. Unprepared or changed container positions will be skipped; compatible neighbors can still migrate.";
            }
            catch(OperationCanceledException){report.State="cancelled";report.Message="Inventory preparation cancelled; no terrain or inventories changed.";}
            catch(Exception e){report.State="failed";report.Message="Inventory preparation failed: "+e.Message;}
            finally
            {
                pool.FreeAll();lock(gate)
                {
                    p.InventoryReport=CopyReport(report);
                    if(loadedMigration.Enabled&&loadedMigration.Plan?.Id==p.Id)lock(loadedGate)
                    {
                        loadedMigration.State=report.State=="complete"?"activating":"preparation-"+report.State;
                        loadedMigration.Message=report.State=="complete"?"Rules and inventory evidence are ready. Backup and activation start automatically; no restart needed.":report.Message;
                        Write(LoadedMigrationFile,loadedMigration);
                        if(report.State=="complete")api.Event.EnqueueMainThreadTask(()=>{lock(gate){if(!disposed&&loadedMigration.Enabled&&loadedMigration.Plan?.Id==p.Id)QueueLiveActivation();}},"migration-inventory-auto-enable");
                    }
                }
            }
        });
    }
    private static MigrationInventoryStore.Entry PrepareMigrationInventoryEntry(Plan p,long chunk,int source,RepairMapping mapping,RestoreCatalog catalog,BlockIdChunkCodec.Retained entity,string history,DateTime cutoff)
    {
        var saved=entity.Summary;var target=catalog.Blocks[mapping.TargetCode];
        if(!target.Supported||saved.Class!=target.Class||saved.InventorySlotCount is not (>=1 and <=64)||saved.SlotsOffset<0||saved.BlockCodeOffset<0||saved.SavedBlockCode.Length==0||saved.StackListTruncated)throw new InvalidOperationException("Compatible complete vanilla container metadata is required. No block entity is invented.");
        if(Canonical(saved.SavedBlockCode)!=mapping.TargetCode&&saved.Class!="Bookshelf")throw new InvalidOperationException("Different container codes require a verified bookshelf slot layout; other containers must keep their exact original code.");
        int[]? usable=null;
        if(saved.Class=="Bookshelf"&&(saved.InventorySlotCount!=14||saved.Material.Length==0||target.UsableSlots==null||!target.UsableSlots.TryGetValue(saved.ShapeType,out usable)))throw new InvalidOperationException("Bookshelf shape/material or target slot layout is incompatible.");
        byte[]? slots=null;InventoryRestoreHistory.Source? snapshot=null;
        if(p.Inventory.Mode=="replace"||saved.NonEmptyStacks==0)
        {
            snapshot=InventoryRestoreHistory.LatestBefore(history,p.World,saved.X!.Value,saved.Y!.Value,saved.Z!.Value,saved.Dimension!.Value,cutoff);
            if(snapshot.Code!=Canonical(saved.SavedBlockCode)||snapshot.SlotCount!=saved.InventorySlotCount)throw new InvalidOperationException("The latest snapshot at the cutoff does not match this retained container's original identity/slot count.");
            var tree=new TreeAttribute();foreach(var slot in snapshot.Slots)
            {
                if(usable!=null&&!usable.Contains(slot.SlotId))throw new InvalidOperationException("Snapshot uses a slot unavailable in this bookshelf shape.");
                if(!catalog.Items.TryGetValue(Canonical(slot.ItemCode),out var item)||slot.StackSize>item.Maximum)throw new InvalidOperationException("Snapshot has a missing, ambiguous or oversized item stack: "+slot.ItemCode);
                tree.SetItemstack(slot.SlotId.ToString(System.Globalization.CultureInfo.InvariantCulture),new ItemStack{Class=item.Kind,Id=item.Id,StackSize=slot.StackSize});
            }
            slots=tree.ToBytes();
        }
        else if(saved.Stacks.Any(s=>!catalog.KnownIds.Contains((s.Kind,s.Id))))throw new InvalidOperationException("Retained inventory contains unresolved item IDs. Restore those item mods first.");
        byte[] payload=MigrationInventoryPayload.Rewrite(entity.Bytes,saved,mapping.TargetCode,slots,p.Inventory.Mode=="replace");
        return new(chunk,saved.X!.Value,saved.Y!.Value,saved.Z!.Value,saved.Dimension!.Value,source,mapping.Target,saved.PayloadSha256,payload,snapshot?.Slots.Sum(s=>(long)s.StackSize)??0,snapshot?.Hash??"",snapshot?.TimestampUtc.Ticks??0);
    }
}
