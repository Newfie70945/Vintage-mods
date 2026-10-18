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
        while(p<blob.Length){int start=p;ulong key=Var();int tag=checked((int)(key>>3)),wire=(int)(key&7);if(tag==0)throw new InvalidDataException("Zero protobuf field.");int data=p,len=0;ulong number=0;
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
    public byte[] Replace(byte[] blob,int source,int target,out long changed)
    {
        if(source==0||target<0||source==target)throw new ArgumentException("Invalid numeric replacement.");
        changed=0;var fields=Fields(blob);var counts=Inspect(blob,id=>id==source);if(!counts.TryGetValue(source,out var found))return blob;
        // Never orphan an inventory or mod block entity when replacing its host.
        // Parse only the standard tree payload, without constructing mod classes.
        foreach(var field in fields.Where(x=>x.Tag==8)){
            if(field.Wire!=2)throw new InvalidDataException("Invalid block-entity payload.");
            using var input=new MemoryStream(blob,field.Data,field.Length,false);using var reader=new BinaryReader(input);reader.ReadString();var tree=new TreeAttribute();tree.FromBytes(reader);
            if(!tree.HasAttribute("posx")||!tree.HasAttribute("posy")||!tree.HasAttribute("posz"))throw new InvalidDataException("Block entity position cannot be verified.");
            int index=((tree.GetInt("posy")&31)*32+(tree.GetInt("posz")&31))*32+(tree.GetInt("posx")&31);
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
