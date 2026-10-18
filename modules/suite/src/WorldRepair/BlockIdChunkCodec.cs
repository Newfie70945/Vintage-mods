using System.Buffers.Binary;
using System.Reflection;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;

namespace BlockLogger.WorldRepair;

// Edits only the solid/fluid palette fields and decor pairs. All other protobuf
// fields are copied byte for byte; no entity or mod block-entity class executes.
internal sealed class BlockIdChunkCodec
{
    internal sealed record Field(int Tag,int Wire,int Start,int End,int Data,int Length,ulong Number);
    private readonly ChunkDataPool pool;
    private readonly int[] compressionScratch=new int[15360];
    private static readonly MethodInfo Unpack=typeof(ChunkDataLayer).GetMethod("Decompress",BindingFlags.Instance|BindingFlags.NonPublic)!;
    public BlockIdChunkCodec(ChunkDataPool pool)=>this.pool=pool;
    public static List<Field> Fields(byte[] blob)
    {
        if(blob.Length>64*1024*1024)throw new InvalidDataException("Chunk blob exceeds the 64 MiB inspection limit.");
        int p=0;var fields=new List<Field>();
        ulong Var(){ulong n=0;for(int i=0;i<10;i++){if(p>=blob.Length)throw new InvalidDataException("Truncated protobuf value.");byte b=blob[p++];if(i==9&&b>1)throw new InvalidDataException("Overflowing protobuf value.");n|=(ulong)(b&127)<<(i*7);if(b<128)return n;}throw new InvalidDataException("Bad protobuf value.");}
        while(p<blob.Length){if(fields.Count>=65536)throw new InvalidDataException("Too many chunk fields to inspect safely.");int start=p;ulong key=Var();int tag=checked((int)(key>>3)),wire=(int)(key&7);if(tag==0)throw new InvalidDataException("Zero protobuf field.");int data=p,len=0;ulong number=0;
            switch(wire){case 0:number=Var();break;case 1:len=8;break;case 2:ulong size=Var();if(size>(ulong)(blob.Length-p))throw new InvalidDataException("Truncated protobuf payload.");data=p;len=(int)size;break;case 5:len=4;break;default:throw new InvalidDataException("Unsupported protobuf wire type.");}
            if(wire!=0)p=checked(p+len);if(p>blob.Length)throw new InvalidDataException("Truncated protobuf field.");fields.Add(new(tag,wire,start,p,data,len,number));
        }
        foreach(int tag in new[]{1,14,15,16})if(fields.Count(x=>x.Tag==tag)>1)throw new InvalidDataException("Duplicate block payload field.");
        return fields;
    }
    private static void Var(Stream stream,ulong value){while(value>=128){stream.WriteByte((byte)(value|128));value>>=7;}stream.WriteByte((byte)value);}
    private ChunkDataLayer Decode(byte[] bytes){var layer=new ChunkDataLayer(pool);try{Unpack.Invoke(layer,[bytes]);if(layer.paletteCount<0||layer.paletteCount>32768)throw new InvalidDataException("Invalid palette size.");return layer;}catch{pool.FreeArrays(layer);throw;}}
    public Dictionary<int,long[]> Inspect(byte[] blob,Func<int,bool> wanted)
    {
        var result=new Dictionary<int,long[]>();var fields=Fields(blob);int version=(int)(fields.LastOrDefault(x=>x.Tag==15)?.Number??0);
        if(version is not (1 or 2))throw new NotSupportedException("Legacy chunk compression requires an official game conversion before repair.");
        void Add(int id,int layer){if(id==0||!wanted(id))return;if(!result.TryGetValue(id,out var count))result[id]=count=new long[3];count[layer]++;}
        foreach(var field in fields.Where(x=>x.Tag is 1 or 16)){
            if(field.Wire!=2)throw new InvalidDataException("Invalid layer field type.");var layer=Decode(blob.AsSpan(field.Data,field.Length).ToArray());
            try{if(layer.palette==null||!layer.palette.Take(layer.paletteCount).Any(wanted))continue;for(int i=0;i<32768;i++)Add(layer.GetUnsafe(i),field.Tag==1?0:1);}finally{pool.FreeArrays(layer);}
        }
        var decor=fields.FirstOrDefault(x=>x.Tag==14);if(decor!=null){if(decor.Wire!=2||decor.Length%8!=0)throw new InvalidDataException("Invalid decor pairs.");for(int i=0;i<decor.Length;i+=8)Add(BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(decor.Data+i+4,4)),2);}
        return result;
    }
    public Dictionary<int,int[]> ReadCells(byte[] blob,IEnumerable<int> indices)
    {
        var cells=indices.Distinct().ToDictionary(i=>i,_=>new int[2]);
        if(cells.Keys.Any(i=>i<0||i>=32768)||cells.Count>257)throw new ArgumentOutOfRangeException(nameof(indices));
        var fields=Fields(blob);int version=(int)(fields.LastOrDefault(x=>x.Tag==15)?.Number??0);
        if(version is not (1 or 2))throw new NotSupportedException("Legacy chunk compression cannot be inspected safely.");
        foreach(var field in fields.Where(x=>x.Tag is 1 or 16)){
            if(field.Wire!=2)throw new InvalidDataException("Invalid layer field type.");var layer=Decode(blob.AsSpan(field.Data,field.Length).ToArray());
            try{foreach(var cell in cells)cell.Value[field.Tag==1?0:1]=layer.GetUnsafe(cell.Key);}finally{pool.FreeArrays(layer);}
        }
        return cells;
    }
    public sealed record Retained(Field Field,byte[] Bytes,SavedBlockEntityInspector.EntitySummary Summary);
    public Retained EntityAt(byte[] blob,int x,int y,int z,int dimension)
    {
        Retained? selected=null;long bytes=0;int count=0;
        foreach(var field in Fields(blob).Where(f=>f.Tag==8)){
            if(field.Wire!=2||++count>256||(bytes+=field.Length)>8*1024*1024)throw new InvalidDataException("Container restore requires verifiable, bounded block-entity records.");
            var data=blob.AsSpan(field.Data,field.Length).ToArray();var entity=SavedBlockEntityInspector.Inspect(data);
            if(!entity.Complete)throw new InvalidDataException("Container record cannot be fully verified: "+entity.Issue);
            if(entity.X==x&&entity.Y==y&&entity.Z==z&&entity.Dimension==dimension){if(selected!=null)throw new InvalidDataException("Duplicate retained containers at this location.");selected=new(field,data,entity);}
        }
        return selected??throw new InvalidOperationException("No retained container metadata exists at this location. Restore a world backup; this tool does not invent block-entity metadata.");
    }
    public byte[] RestoreContainer(byte[] blob,int x,int y,int z,int dimension,int expectedSource,int target,string entityHash,byte[]? slots)
    {
        if(target<=0)throw new ArgumentException("A registered non-air container target is required.");
        int index=((y&31)*32+(z&31))*32+(x&31);if(ReadCells(blob,[index])[index][0]!=expectedSource)throw new InvalidOperationException("Saved block changed since preview.");
        var entity=EntityAt(blob,x,y,z,dimension);if(entity.Summary.PayloadSha256!=entityHash)throw new InvalidOperationException("Retained container changed since preview.");
        if(slots!=null&&(entity.Summary.NonEmptyStacks>0||entity.Summary.SlotsOffset<0))throw new InvalidOperationException("Snapshot restoration requires an empty standard inventory; current contents are never overwritten.");
        byte[]? solidBytes=null,entityBytes=null;
        if(expectedSource!=target){var solid=Fields(blob).Single(f=>f.Tag==1&&f.Wire==2);var layer=new BlockChunkDataLayer(pool);
            try{Unpack.Invoke(layer,[blob.AsSpan(solid.Data,solid.Length).ToArray()]);layer.Set(index,target);solidBytes=ChunkDataLayer.Compress(layer,compressionScratch);}finally{pool.FreeArrays(layer);}}
        if(slots!=null){using var data=new MemoryStream();int offset=entity.Summary.SlotsOffset,end=offset+entity.Summary.SlotsLength;data.Write(entity.Bytes,0,offset);data.Write(slots);data.Write(entity.Bytes,end,entity.Bytes.Length-end);entityBytes=data.ToArray();
            var check=SavedBlockEntityInspector.Inspect(entityBytes);if(!check.Complete)throw new InvalidDataException("Generated inventory could not be verified.");}
        if(solidBytes==null&&entityBytes==null)throw new InvalidOperationException("The saved container block already matches; no reconnect is needed.");
        using var output=new MemoryStream();foreach(var field in Fields(blob)){byte[]? replace=field.Tag==1?solidBytes:field.Start==entity.Field.Start?entityBytes:null;
            if(replace!=null){Var(output,(ulong)((field.Tag<<3)|2));Var(output,(ulong)replace.Length);output.Write(replace);}else output.Write(blob,field.Start,field.End-field.Start);}
        return output.ToArray();
    }
    public byte[] ReplaceAt(byte[] blob,int x,int y,int z,int dimension,int expectedSource,int target)
    {
        if(target<0||expectedSource<0||target==expectedSource)throw new ArgumentException("Choose a different registered destination.");
        int index=((y&31)*32+(z&31))*32+(x&31);var fields=Fields(blob);
        var cell=ReadCells(blob,[index])[index];if(cell[0]!=expectedSource)throw new InvalidOperationException("Selected saved block changed after preview.");
        if(cell[1]!=0)throw new InvalidOperationException("Selected position contains fluid; one-position repair supports the solid layer only.");
        long bytes=0;int count=0;
        foreach(var field in fields.Where(f=>f.Tag==8))
        {
            if(field.Wire!=2||++count>256||(bytes+=field.Length)>8*1024*1024)throw new InvalidDataException("Retained block-entity records exceed verification bounds.");
            var entity=SavedBlockEntityInspector.Inspect(blob.AsSpan(field.Data,field.Length).ToArray());
            if(!entity.Complete)throw new InvalidDataException("Unverifiable retained metadata; selected repair refused. "+entity.Issue);
            if(entity.X==x&&entity.Y==y&&entity.Z==z&&entity.Dimension==dimension)throw new InvalidOperationException("Selected position retains block-entity/inventory metadata. Use retained inventory restoration; generic replacement would lose that data.");
        }
        var solid=fields.Single(f=>f.Tag==1&&f.Wire==2);var layer=new BlockChunkDataLayer(pool);byte[] compressed;
        try{Unpack.Invoke(layer,[blob.AsSpan(solid.Data,solid.Length).ToArray()]);layer.Set(index,target);compressed=ChunkDataLayer.Compress(layer,compressionScratch);}finally{pool.FreeArrays(layer);}
        using var output=new MemoryStream();foreach(var field in fields){if(field.Start==solid.Start){Var(output,(ulong)((field.Tag<<3)|2));Var(output,(ulong)compressed.Length);output.Write(compressed);}else output.Write(blob,field.Start,field.End-field.Start);}
        return output.ToArray();
    }
    public byte[] Replace(byte[] blob,int source,int target,out long changed)
    {
        if(source==0||target<0||source==target)throw new ArgumentException("Invalid numeric replacement.");
        changed=0;var fields=Fields(blob);var counts=Inspect(blob,id=>id==source);if(!counts.TryGetValue(source,out var found))return blob;
        // Never orphan an inventory or mod block entity when replacing its host.
        // Parse only the standard tree payload, without constructing mod classes.
        foreach(var field in fields.Where(x=>x.Tag==8)){
            if(field.Wire!=2)throw new InvalidDataException("Invalid block-entity payload.");
            var retained=SavedBlockEntityInspector.Inspect(blob.AsSpan(field.Data,field.Length).ToArray());
            if(!retained.Complete)throw new InvalidDataException("Saved block-entity data cannot be fully verified; generic replacement refused. Inspect the chunk for a dedicated migration. "+retained.Issue);
            int index=(((retained.InternalY!.Value&31)*32+(retained.Z!.Value&31))*32)+(retained.X!.Value&31);
            foreach(var solid in fields.Where(x=>x.Tag is 1 or 16)){var layer=Decode(blob.AsSpan(solid.Data,solid.Length).ToArray());try{if(layer.GetUnsafe(index)==source)throw new InvalidOperationException("Source block hosts a block entity; inventory-safe replacement requires restoring its mod or a dedicated migration.");}finally{pool.FreeArrays(layer);}}
        }
        var replacements=new Dictionary<int,byte[]>();
        foreach(var field in fields.Where(x=>x.Tag is 1 or 16)){
            if(found[field.Tag==1?0:1]==0)continue;var layer=Decode(blob.AsSpan(field.Data,field.Length).ToArray());
            try{for(int i=0;i<layer.paletteCount;i++)if(layer.palette[i]==source)layer.palette[i]=target;replacements[field.Tag]=ChunkDataLayer.Compress(layer,compressionScratch);}finally{pool.FreeArrays(layer);}
        }
        var decor=fields.FirstOrDefault(x=>x.Tag==14);if(decor!=null&&found[2]>0){using var output=new MemoryStream();for(int i=0;i<decor.Length;i+=8){var pair=blob.AsSpan(decor.Data+i,8);int id=BinaryPrimitives.ReadInt32LittleEndian(pair[4..]);if(id==source){if(target==0)continue;var copy=pair.ToArray();BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(4),target);output.Write(copy);}else output.Write(pair);}replacements[14]=output.ToArray();}
        using var rewritten=new MemoryStream();foreach(var field in fields){if(replacements.TryGetValue(field.Tag,out var bytes)){Var(rewritten,(ulong)((field.Tag<<3)|2));Var(rewritten,(ulong)bytes.Length);rewritten.Write(bytes);}else rewritten.Write(blob,field.Start,field.End-field.Start);}
        changed=found.Sum();return rewritten.ToArray();
    }
}
