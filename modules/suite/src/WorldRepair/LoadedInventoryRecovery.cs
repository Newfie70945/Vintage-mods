using Vintagestory.Common.Database;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private readonly HashSet<int> loadedInventorySources=[];
    private string loadedManifest="",loadedInventoryWorld="",loadedInventoryPlan="";
    private long loadedInventoriesRecovered,loadedInventoryItems,loadedInventoriesPreserved,loadedInventorySkipped;private string loadedInventoryLastIssue="";
    private Dictionary<int,int> loadedPlainReplacements=[];
    private sealed record InventoryLoad(byte[] Bytes,List<MigrationInventoryStore.Entry> Entries,long Recovered,long Preserved,long Items,long ContainerBlocks,long Skipped,string Issue);
    private InventoryLoad PrepareLoadedInventories(byte[] original,Dictionary<int,long[]> counts)
    {
        if(!counts.Keys.Any(loadedInventorySources.Contains))return new(original,[],0,0,0,0,0,"");
        var codec=loadedCodec!;var fields=BlockIdChunkCodec.Fields(original).Where(f=>f.Tag==8).ToArray();
        if(fields.Length is 0 or >256||fields.Sum(f=>(long)f.Length)>8*1024*1024)throw new InvalidDataException("No bounded retained container records exist for inventory recovery.");
        var records=fields.Select(f=>codec.EntityAtBytes(original,f)).ToList();var first=records[0].Summary;
        long chunk=unchecked((long)ChunkPos.ToChunkIndex(first.X!.Value/32,first.Y!.Value/32,first.Z!.Value/32,first.Dimension!.Value));
        var entries=MigrationInventoryStore.ReadChunk(loadedManifest,chunk).ToDictionary(e=>(e.X,e.Y,e.Z,e.Dimension));
        var cells=codec.ReadCells(original,records.Select(e=>(((e.Summary.InternalY!.Value&31)*32+(e.Summary.Z!.Value&31))*32)+(e.Summary.X!.Value&31)));
        var replacements=new Dictionary<int,byte[]>();var positions=new Dictionary<int,(int Source,int Target)>();long skipped=0;string issue="";using var receipts=MigrationInventoryStore.Connect(InventoryReceipts,true);var used=new List<MigrationInventoryStore.Entry>();long recovered=0,preserved=0,items=0;
        foreach(var record in records)
        {
            var saved=record.Summary;int index=(((saved.InternalY!.Value&31)*32+(saved.Z!.Value&31))*32)+(saved.X!.Value&31);int source=cells[index][0];if(!loadedInventorySources.Contains(source))continue;
            try
            {
                if(!entries.TryGetValue((saved.X.Value,saved.Y!.Value,saved.Z.Value,saved.Dimension!.Value),out var entry))throw new InvalidOperationException("Container has no approved recovery entry. Review the preparation's skipped locations; no items will be invented.");
                if(entry.Source!=source||entry.Target!=loadedReplacements[source]||entry.EntityHash!=saved.PayloadSha256)throw new InvalidOperationException("Container contents/metadata changed after inventory review; migration skipped to protect current items.");
                var check=SavedBlockEntityInspector.Inspect(entry.Payload);var identity=loadedIdentities[source];
                if(!check.Complete||check.Class!=identity.EntityClass||check.SavedBlockCode!=identity.Code||check.X!=saved.X||check.InternalY!=saved.InternalY||check.Z!=saved.Z||check.InventorySlotCount!=saved.InventorySlotCount)throw new InvalidDataException("Prepared container no longer matches this target/location.");
                if(entry.SnapshotHash.Length>0)MigrationInventoryStore.CheckReceipt(receipts,null,loadedInventoryWorld,entry);
                positions.Add(index,(source,entry.Target));replacements[record.Field.Start]=entry.Payload;used.Add(entry);
                if(entry.SnapshotHash.Length>0){recovered++;items+=entry.Items;}else preserved++;
            }
            catch(Exception e)when(e is InvalidOperationException or InvalidDataException)
            {skipped++;issue=e.Message;}

        }
        return new(codec.ReplaceVerifiedContainerCells(codec.ReplaceEntityPayloads(original,replacements),positions),used,recovered,preserved,items,positions.Count,skipped,issue);
    }
}
