namespace BlockLogger.WorldRepair;

// Detection only: registered IDs can still host orphaned metadata after remaps.
public static class RetainedBlockFindings
{
    public sealed record Finding(int X,int Y,int Z,int Dimension,int SolidId,string CurrentCode,string SavedCode,string Class,int Stacks,string Reason);
    internal static List<Finding> Inspect(byte[] blob,BlockIdChunkCodec codec,Func<int,string> code,Func<int,string> entityClass,out int unsupported)
    {
        unsupported=0;var entities=new List<SavedBlockEntityInspector.EntitySummary>();long bytes=0;int records=0;
        foreach(var f in BlockIdChunkCodec.Fields(blob).Where(f=>f.Tag==8)){
            if(f.Wire!=2||++records>256||(bytes+=f.Length)>8*1024*1024){unsupported++;break;}
            var entity=SavedBlockEntityInspector.Inspect(blob.AsSpan(f.Data,f.Length).ToArray());
            if(!entity.Complete){unsupported++;continue;}entities.Add(entity);
        }
        if(entities.Count==0)return [];
        var cells=codec.ReadCells(blob,entities.Select(e=>((e.Y!.Value&31)*32+(e.Z!.Value&31))*32+(e.X!.Value&31)));
        return entities.Select(e=>{
            int index=((e.Y!.Value&31)*32+(e.Z!.Value&31))*32+(e.X!.Value&31),id=cells[index][0];string saved=e.SavedBlockCode.Length==0?"":e.SavedBlockCode.Contains(':')?e.SavedBlockCode:"game:"+e.SavedBlockCode;
            return new Finding(e.X!.Value,e.Y!.Value,e.Z!.Value,e.Dimension!.Value,id,code(id),saved,e.Class,e.NonEmptyStacks,"Retained class/code differs from current host. Inspect before restoring; never globally remap air or overwrite its inventory.");
        }).Where(e=>e.Class!=entityClass(e.SolidId)||e.SavedCode.Length>0&&e.SavedCode!=e.CurrentCode).ToList();
    }
    public static bool MappingMismatch(int id,string? saved,string? current)=>id>0&&!string.IsNullOrWhiteSpace(saved)&&!string.IsNullOrWhiteSpace(current)&&(saved.Contains(':')?saved:"game:"+saved)!=(current.Contains(':')?current:"game:"+current);
}
