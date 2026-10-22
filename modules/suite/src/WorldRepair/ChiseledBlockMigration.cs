using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private long migratedMaterialReferences,skippedMaterialReferences,refusedChiseledEntities;
    private string lastChiseledIssue="";
    private BlockPos MigrationTargetPosition(IServerPlayer player,string[] tokens)
    {
        string? at=tokens.FirstOrDefault(t=>t.StartsWith("at=",StringComparison.Ordinal));
        var selected=player.CurrentBlockSelection?.Position?.Copy();
        if(at==null&&tokens.Any(t=>t.StartsWith("dimension=",StringComparison.Ordinal)))throw new ArgumentException("dimension=N accompanies at=X,Y,Z; otherwise the looked-at block determines the dimension.");
        if(at==null)return selected??throw new ArgumentException("Look directly at the block, or use at=X,Y,Z dimension=N for an unselectable chiseled block.");
        var parts=at[3..].Split(',');
        if(parts.Length!=3||!int.TryParse(parts[0],out int x)||!int.TryParse(parts[1],out int y)||!int.TryParse(parts[2],out int z)||x<0||y<0||y>=32768||z<0)
            throw new ArgumentException("Use at=X,Y,Z with valid non-negative block coordinates and local Y below 32768.");
        int dimension;
        string? dim=tokens.FirstOrDefault(t=>t.StartsWith("dimension=",StringComparison.Ordinal));
        if(dim!=null)
        {
            if(!int.TryParse(dim[10..],out dimension)||dimension<0||dimension>511)
                throw new ArgumentException("Use dimension=N between 0 and 511.");
        }
        else if(selected!=null) dimension=selected.dimension;
        else if(!EntityPositionResolver.TryGetCoordinates(player.Entity,out _,out _,out _,out dimension))
            throw new InvalidOperationException("Player dimension is unavailable. Specify at=X,Y,Z dimension=N explicitly; no blocks were changed.");
        var pos=new BlockPos(x,y,z,dimension);
        if(api.World.BlockAccessor.GetChunkAtBlockPos(pos)==null)throw new InvalidOperationException("Target terrain must already be loaded; this command does not load terrain.");
        return pos;
    }
    private int SelectMissingMaterial(int[]? materials,string[] tokens)
    {
        var missing=(materials??[]).Where(id=>id>0&&Missing(id)).Distinct().ToArray();
        string? source=tokens.FirstOrDefault(t=>t.StartsWith("source=",StringComparison.Ordinal));
        if(source!=null)
        {
            if(!int.TryParse(source[7..],out int id)||!missing.Contains(id))throw new ArgumentException("source=ID must name a missing material in this chiseled block. Missing IDs: "+string.Join(", ",missing));
            return id;
        }
        if(missing.Length==1)return missing[0];
        throw new ArgumentException(missing.Length==0?"This chiseled block has no missing numeric material IDs. Its registered host and healthy materials are protected.":"Several materials are missing: "+string.Join(", ",missing)+". Repeat replaceheld with source=ID to choose one; the chiseled shape stays intact.");
    }
    private string InspectMigrationTarget(IServerPlayer player,string arguments)
    {
        var tokens=arguments.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Any(t=>!t.StartsWith("at=",StringComparison.Ordinal)&&!t.StartsWith("dimension=",StringComparison.Ordinal)))throw new ArgumentException("inspect options: at=X,Y,Z dimension=N.");
        var pos=MigrationTargetPosition(player,tokens);int id=api.World.BlockAccessor.GetBlockId(pos);
        var map=Mapping();var rules=CopyConfiguredRules();var entity=api.World.BlockAccessor.GetBlockEntity(pos);
        string DescribeRule(int source){var rule=rules.FirstOrDefault(r=>r.Source==source);return rule==null?"no saved rule":$"saved rule → {rule.TargetCode} (ID {rule.Target}); migration {loadedMigration.State}";}
        string info=$"Target {pos.X},{pos.Y},{pos.Z} dimension {pos.dimension}: solid ID {id}; saved identity {Label(id,map)}; missing={Missing(id)}; {DescribeRule(id)}.";
        info+=entity==null?" No live block entity. This does not establish whether saved metadata or historical inventory evidence survives.":$" Live block entity: {api.World.ClassRegistry.GetBlockEntityClass(entity.GetType())}. Its presence alone does not verify recoverable contents; inspect saved inventory evidence in the webpanel.";
        if(api.World.BlockAccessor.GetBlockEntity(pos) is BlockEntityMicroBlock micro)
            info+=" Chiseled material IDs: "+string.Join(", ",(micro.BlockIds??[]).Distinct().Select(m=>$"{m}{(Missing(m)?" (missing) ["+DescribeRule(m)+"]":" (registered)")}"))+". Use replaceheld source=ID when more than one is missing. Geometry is preserved.";
        return info+" Hold a replacement and use /nseblockmigrate previewheld to check it without saving a rule.";
    }
    private bool ChiseledTargetAllowed(int id)=>id>0&&!Missing(id)&&!api.World.Blocks[id].ForFluidsLayer&&string.IsNullOrEmpty(api.World.Blocks[id].EntityClass);
    private byte[] RewriteSavedChiseledMaterials(byte[] original,out long changed)
    {
        changed=0;var payloads=new Dictionary<int,byte[]>();
        foreach(var field in BlockIdChunkCodec.Fields(original).Where(f=>f.Tag==8))
        {
            if(field.Wire!=2||field.Length<11||original[field.Data]!=10||!original.AsSpan(field.Data+1,10).SequenceEqual("MicroBlock"u8))continue;
            try
            {
                if(field.Length>2*1024*1024)throw new InvalidDataException("Chiseled metadata exceeds verification limits.");
                byte[] bytes=original.AsSpan(field.Data,field.Length).ToArray();
                var rewritten=ChiseledMaterialPayload.Rewrite(bytes,loadedReplacements,Missing,ChiseledTargetAllowed,out long count,out long skipped);
                skippedMaterialReferences+=skipped;changed+=count;
                if(count>0)payloads[field.Start]=rewritten;
            }
            catch(Exception error)when(error is InvalidDataException or NotSupportedException or ArgumentException)
            {refusedChiseledEntities++;lastChiseledIssue=error.Message;}
        }
        return payloads.Count>0?loadedCodec!.ReplaceEntityPayloads(original,payloads):original;
    }
    private long MigrateLiveChiseled(BlockEntityMicroBlock micro)
    {
        var original=micro.BlockIds;if(micro.GetType()!=typeof(BlockEntityMicroBlock)||original==null||original.Length>256)return 0;
        int[]? replacements=null;long changed=0;
        for(int i=0;i<original.Length;i++)
        {
            int source=original[i];if(!Missing(source)||!loadedReplacements.TryGetValue(source,out int target))continue;
            if(!ChiseledTargetAllowed(target)){skippedMaterialReferences++;continue;}
            replacements??=(int[])original.Clone();replacements[i]=target;changed++;
        }
        if(replacements==null)return 0;
        micro.BlockIds=replacements;
        micro.MarkMeshDirty();micro.MarkDirty(true);
        // Selection bounds depend on render pass; regenerate only when every
        // material resolves, so another missing material cannot throw here.
        if(replacements.All(id=>!Missing(id)))micro.RegenSelectionBoxes(api.World,null);
        return changed;
    }
}
