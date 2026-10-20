using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private readonly HashSet<int> loadedReconnectSources=[];
    private RestoreCatalog? loadedReconnectCatalog;
    private InventoryLoad PrepareLoadedReconnections(byte[] original,Dictionary<int,long[]> counts)
    {
        if(loadedReconnectCatalog==null||!counts.Keys.Any(loadedReconnectSources.Contains))return new(original,[],0,0,0,0,0,"");
        var codec=loadedCodec!;var fields=BlockIdChunkCodec.Fields(original).Where(f=>f.Tag==8).ToArray();
        if(fields.Length>32768||fields.Sum(f=>(long)f.Length)>32*1024*1024)throw new InvalidDataException("Retained reconnection metadata exceeds bounded chunk limits.");
        var records=fields.Select(f=>codec.EntityAtBytes(original,f)).ToList();
        var cells=codec.ReadCells(original,records.Select(e=>(((e.Summary.InternalY!.Value&31)*32+(e.Summary.Z!.Value&31))*32)+(e.Summary.X!.Value&31)));
        var positions=new Dictionary<int,(int Source,int Target)>();var payloads=new Dictionary<int,byte[]>();var hosted=new Dictionary<int,int>();var unique=new HashSet<int>();long skipped=0;string issue="";
        foreach(var record in records)
        {
            var saved=record.Summary;int index=(((saved.InternalY!.Value&31)*32+(saved.Z!.Value&31))*32)+(saved.X!.Value&31);
            if(records.Count>0&&(saved.X/32!=records[0].Summary.X/32||saved.InternalY/32!=records[0].Summary.InternalY/32||saved.Z/32!=records[0].Summary.Z/32))throw new InvalidDataException("Out-of-chunk retained container; reconnection refused.");
            if(!unique.Add(index))throw new InvalidDataException("Duplicate retained positions; container reconnection refused.");
            int source=cells[index][0];if(!loadedReconnectSources.Contains(source))continue;
            hosted[source]=hosted.GetValueOrDefault(source)+1;
            try
            {
                if(cells[index][1]!=0)throw new InvalidOperationException("Container source contains fluid; reconnection refused.");
                var mapping=EffectiveMappings(loadedMigration.Plan!).Single(m=>m.Source==source);
                var entry=PrepareMigrationInventoryEntry(loadedMigration.Plan!,0,source,mapping,loadedReconnectCatalog,record,"",DateTime.UtcNow);
                positions[index]=(source,mapping.Target);payloads[record.Field.Start]=entry.Payload;
            }
            catch(Exception error)when(error is InvalidOperationException or InvalidDataException or ArgumentException)
            {skipped++;issue=$"Container {saved.X},{saved.Y},{saved.Z}: {error.Message}";}
        }
        foreach(var count in counts.Where(c=>loadedReconnectSources.Contains(c.Key)))
            if(count.Value[0]>hosted.GetValueOrDefault(count.Key)){skipped+=count.Value[0]-hosted.GetValueOrDefault(count.Key);issue="Missing retained container metadata; reconnect cannot invent an inventory or bookshelf shape. Use historical recovery or restore original mod/world data.";}
        var rewritten=codec.ReplaceVerifiedContainerCells(codec.ReplaceEntityPayloads(original,payloads),positions);
        return new(rewritten,[],0,positions.Count,0,positions.Count,skipped,issue);
    }
    private bool TryReconnectLive(BlockPos pos,int source,int target,BlockEntity retained)
    {
        if(loadedReconnectCatalog==null||!Missing(source)||api.World.BlockAccessor.GetBlockId(pos)!=source||
            api.World.BlockAccessor.GetChunkAtBlockPos(pos)?.UnpackAndReadBlock(((pos.Y&31)*32+(pos.Z&31))*32+(pos.X&31),BlockLayersAccess.Fluid)!=0)return false;
        string entityClass=api.World.ClassRegistry.GetBlockEntityClass(retained.GetType());
        if(entityClass!=api.World.Blocks[target].EntityClass)return false;
        var tree=new TreeAttribute();retained.ToTreeAttributes(tree);using var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,Encoding.UTF8,true)){writer.Write(entityClass);tree.ToBytes(writer);}
        byte[] bytes=stream.ToArray();var summary=SavedBlockEntityInspector.Inspect(bytes);
        if(!summary.Complete||summary.X!=pos.X||summary.Y!=pos.Y||summary.Z!=pos.Z||summary.Dimension!=pos.dimension)return false;
        var mapping=EffectiveMappings(loadedMigration.Plan!).Single(m=>m.Source==source);
        _=PrepareMigrationInventoryEntry(loadedMigration.Plan!,0,source,mapping,loadedReconnectCatalog,new(new(8,2,0,bytes.Length,0,bytes.Length,0),bytes,summary),"",DateTime.UtcNow);
        // Validation never assigns prepared slots. Keep the existing inventory
        // object and ItemStacks, including text/freshness/custom attributes.
        api.World.BlockAccessor.ExchangeBlock(target,pos);retained.MarkDirty(true);api.World.BlockAccessor.MarkBlockDirty(pos);
        return true;
    }
}
