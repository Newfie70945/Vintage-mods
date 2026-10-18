using System.Globalization;
using System.Security.Cryptography;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.API.Datastructures;
using Vintagestory.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed class Inspection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string State { get; set; } = "running";
        public string World { get; set; } = "";
        public string ChunkKey { get; set; } = "";
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? Z { get; set; }
        public int Dimension { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public bool ChunkFound { get; set; }
        public string ChunkSha256 { get; set; } = "";
        public int? SolidId { get; set; }
        public string SolidCode { get; set; } = "";
        public int? FluidId { get; set; }
        public string FluidCode { get; set; } = "";
        public int BlockEntityRecords { get; set; }
        public int UndecodedRecords { get; set; }
        public bool Limited { get; set; }
        public List<SavedBlockEntityInspector.EntitySummary> Entities { get; set; } = [];
        public string Message { get; set; } = "";
        public string Limits { get; set; } = "Saved data only. No terrain loaded, generated or changed. No item or block-entity classes executed. Retained stacks are evidence, not proof of recoverability; opaque/custom data may remain undecoded. Current registry code labels do not migrate stack IDs. No inventory restoration is performed.";
    }
    private Inspection? inspection;
    private string? historyDatabasePath;
    private BlockHistoryAdvisor.Report? recommendations;
    public void ConfigureHistoryDatabase(string path)=>historyDatabasePath=path;
    public object StartRecommendations(int x,int y,int z,int dimension,string beforeUtc="")
    {
        lock(gate)
        {
            if(disposed)throw new ObjectDisposedException(nameof(BlockIdRepairService));
            if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for the current scan/inspection, or cancel it first.");
            if(x<0||z<0||x/32>4194303||z/32>4194303||y<0||y/32>511||dimension<0||dimension>1023)throw new ArgumentException("Use absolute saved X/Y/Z and a valid dimension.");
            DateTime cutoff=DateTime.UtcNow;
            if(beforeUtc.Length>0){if(!DateTimeOffset.TryParse(beforeUtc,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time))throw new ArgumentException("Use an ISO-8601 UTC history cutoff.");cutoff=time.UtcDateTime;}
            var report=new BlockHistoryAdvisor.Report{World=api.World.SavegameIdentifier,X=x,Y=y,Z=z,Dimension=dimension,BeforeUtc=cutoff};
            recommendations=new BlockHistoryAdvisor.Report{Id=report.Id,World=report.World,X=x,Y=y,Z=z,Dimension=dimension,BeforeUtc=cutoff};
            var targets=api.World.Blocks.Where(b=>b?.Code!=null&&!b.IsMissing).GroupBy(b=>b.Code.ToString(),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>new BlockHistoryAdvisor.Target(g.First().BlockId,g.First().EntityClass??""),StringComparer.Ordinal);
            string path=historyDatabasePath??"";cancel?.Dispose();cancel=new();var token=cancel.Token;
            worker=Task.Run(()=>{try{BlockHistoryAdvisor.Read(path,report,targets,token);}catch(OperationCanceledException){report.State="cancelled";report.Message="History lookup cancelled.";}catch(Exception e){report.State="failed";report.Message="History lookup failed; no replacement inferred. "+e.Message;}finally{lock(gate){recommendations=report;}}});
            return new{success=true,recommendationId=report.Id};
        }
    }
    public object SearchTargets(string query)
    {
        query=query.Trim();if(query.Length==0||query.Length>128||(query.Length<2&&!int.TryParse(query,out _)))throw new ArgumentException("Enter 2–128 characters of a block code, or an exact numeric ID.");
        int.TryParse(query,NumberStyles.Integer,CultureInfo.InvariantCulture,out int numeric);
        bool isNumber=int.TryParse(query,out _);
        var matches=api.World.Blocks.Where(b=>b?.Code!=null&&!b.IsMissing&&(isNumber?b.BlockId==numeric:b.Code.ToString().Contains(query,StringComparison.OrdinalIgnoreCase)))
            .Take(51).Select(b=>new{id=b.BlockId,code=b.Code.ToString(),entityClass=b.EntityClass??"",fluid=b.ForFluidsLayer,selectable=string.IsNullOrEmpty(b.EntityClass),reason=string.IsNullOrEmpty(b.EntityClass)?"":"Block entity: dedicated migration required; generic remap blocked."}).ToArray();
        return new{results=matches.Take(50).ToArray(),limited=matches.Length>50};
    }
    public object StartInspection(int x,int y,int z,int dimension,string chunkKey="")
    {
        Guard();lock(gate)
        {
            if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for the current scan/inspection, or cancel it first.");
            if(!SavedBlockEntityInspector.BuiltinTypesSupported())throw new NotSupportedException("A mod changed built-in attribute serialization; inventory inspection is disabled.");
            long key;bool location=string.IsNullOrWhiteSpace(chunkKey);
            if(location)
            {
                if(x<0||z<0||x/32>4194303||z/32>4194303||y<0||y/32>511||dimension<0||dimension>1023)throw new ArgumentException("Use absolute saved coordinates: X/Z ≥ 0, Y 0–16383 and dimension 0–1023. Spawn-relative coordinates are not accepted.");
                key=unchecked((long)ChunkPos.ToChunkIndex(x/32,y/32,z/32,dimension));
            }
            else if(!long.TryParse(chunkKey,NumberStyles.Integer,CultureInfo.InvariantCulture,out key))throw new ArgumentException("Use the exact signed chunk key from a scan sample.");
            var pos=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)key));
            var report=new Inspection{World=api.World.SavegameIdentifier,ChunkKey=key.ToString(CultureInfo.InvariantCulture),Dimension=pos.Dimension,X=location?x:null,Y=location?y:null,Z=location?z:null};
            var blockCodes=api.World.Blocks.Select(b=>b?.Code?.ToString()??"").ToArray();var entityClasses=api.World.Blocks.Select(b=>b?.EntityClass??"").ToArray();var itemCodes=api.World.Items.Select(i=>i?.Code?.ToString()??"").ToArray();
            string path=Database().DatabaseFilename;var server=(ServerMain)api.World;inspection=new Inspection{Id=report.Id,World=report.World,ChunkKey=report.ChunkKey,X=report.X,Y=report.Y,Z=report.Z,Dimension=report.Dimension};
            cancel?.Dispose();cancel=new();var token=cancel.Token;
            worker=Task.Run(()=>{
                try{InspectSaved(report,path,key,pos,blockCodes,entityClasses,itemCodes,server,token);}
                catch(OperationCanceledException){report.State="cancelled";report.Message="Inspection cancelled; no world data changed.";}
                catch(Exception e){report.State="failed";report.Message=e.Message;}
                finally{lock(gate){inspection=report;}}
            });return new{success=true,inspectionId=report.Id};
        }
    }
    private static void InspectSaved(Inspection result,string path,long key,ChunkPos chunk,string[] blockCodes,string[] entityClasses,string[] itemCodes,ServerMain server,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();byte[] blob;
        using(var connection=Open(path))using(var cmd=connection.CreateCommand())
        {
            cmd.CommandText="SELECT data FROM chunk WHERE position=$key";cmd.CommandTimeout=2;cmd.Parameters.AddWithValue("$key",key);
            using var reader=cmd.ExecuteReader();
            if(!reader.Read()){result.State="complete";result.Message="No saved chunk at this location. Nothing was loaded or generated.";return;}
            long length=reader.GetBytes(0,0,null,0,0);if(length>64*1024*1024)throw new InvalidDataException("Saved chunk exceeds the inspection size limit.");blob=(byte[])reader[0];
        }
        result.ChunkFound=true;result.ChunkSha256=Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();
        var fields=BlockIdChunkCodec.Fields(blob).Where(f=>f.Tag==8).ToArray();result.BlockEntityRecords=fields.Length;long inspectedBytes=0;int inspectedRecords=0;
        foreach(var field in fields)
        {
            token.ThrowIfCancellationRequested();
            if(inspectedRecords++>=256||inspectedBytes+field.Length>8*1024*1024){result.Limited=true;break;}
            if(field.Wire!=2)throw new InvalidDataException("Invalid saved block-entity field.");inspectedBytes+=field.Length;
            var entity=SavedBlockEntityInspector.Inspect(blob.AsSpan(field.Data,field.Length).ToArray());
            bool positioned=entity.X.HasValue&&entity.InternalY.HasValue&&entity.Z.HasValue;
            bool sameChunk=positioned&&entity.X>=0&&entity.InternalY>=0&&entity.Z>=0&&entity.X/32==chunk.X&&entity.Y/32==chunk.Y&&entity.Z/32==chunk.Z&&entity.Dimension==chunk.Dimension;
            if(!entity.Complete||!sameChunk){result.UndecodedRecords++;if(positioned&&!sameChunk){entity.Complete=false;entity.Issue="Saved position does not match this chunk; no block association assumed.";}}
            if(result.X.HasValue&&sameChunk&&(entity.X!=result.X||entity.Y!=result.Y||entity.Z!=result.Z))continue;
            result.Entities.Add(entity);
        }
        var indices=result.Entities.Where(e=>e.Complete).Select(e=>((e.Y!.Value&31)*32+(e.Z!.Value&31))*32+(e.X!.Value&31)).ToList();
        int selected=result.X.HasValue?((result.Y!.Value&31)*32+(result.Z!.Value&31))*32+(result.X.Value&31):-1;if(selected>=0)indices.Add(selected);
        var pool=new ChunkDataPool(32,server){CacheSize=4};
        try
        {
            var cells=new BlockIdChunkCodec(pool).ReadCells(blob,indices);
            string Code(int id,string[] codes)=>id>=0&&id<codes.Length?codes[id]:"";
            if(selected>=0){var ids=cells[selected];result.SolidId=ids[0];result.SolidCode=Code(ids[0],blockCodes);result.FluidId=ids[1];result.FluidCode=Code(ids[1],blockCodes);}
            foreach(var entity in result.Entities)
            {
                if(!entity.Complete){entity.Assessment="Partial or unsupported data. Do not infer that an inventory is empty or lost.";continue;}
                int index=((entity.Y!.Value&31)*32+(entity.Z!.Value&31))*32+(entity.X!.Value&31);int id=cells[index][0];entity.SolidId=id;entity.SolidCode=Code(id,blockCodes);entity.CurrentEntityClass=Code(id,entityClasses);
                foreach(var stack in entity.Stacks)stack.CurrentCode=stack.Kind=="Block"?Code(stack.Id,blockCodes):stack.Kind=="Item"?Code(stack.Id,itemCodes):"";
                string savedCode=entity.SavedBlockCode.Contains(':')?entity.SavedBlockCode:"game:"+entity.SavedBlockCode;
                bool mismatch=entity.Class!=entity.CurrentEntityClass||entity.SavedBlockCode.Length>0&&savedCode!=entity.SolidCode;
                entity.Assessment=mismatch?"Retained block entity differs from its current block. Dedicated recovery assessment required; generic remapping is blocked.":"Retained block entity matches the current block class/code. Inventory compatibility is not yet verified.";
                entity.Assessment+=entity.NonEmptyStacks>0?" Serialized item stacks found; no items have been restored.":" No non-empty built-in item stacks decoded; custom/opaque storage may still exist.";
            }
        }finally{pool.FreeAll();}
        result.State="complete";result.Message="Read-only saved-data inspection complete. "+(result.Entities.Count==0?"No retained block entity matched. Check coordinates and inspection limits; absence is not proof of deleted inventory.":"Review retained data and any mismatches before considering a dedicated migration.");
    }
}
