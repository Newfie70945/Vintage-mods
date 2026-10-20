using System.Buffers.Binary;

namespace BlockLogger.WorldRepair;

// Only numeric material entries change. Cuboids, rotation, names, decor and all
// other saved bytes are left untouched; no block entity is constructed here.
internal static class ChiseledMaterialPayload
{
    internal static byte[] Rewrite(byte[] original,IReadOnlyDictionary<int,int> rules,Func<int,bool> sourceMissing,Func<int,bool> targetAllowed,out long changed,out long skipped)
    {
        changed=0;skipped=0;
        if(original.Length<11||original[0]!=10||!original.AsSpan(1,10).SequenceEqual("MicroBlock"u8))return original;
        var saved=SavedBlockEntityInspector.Inspect(original);
        if(!saved.Complete||saved.MaterialIdsOffset<0||saved.MaterialIds==null)
            throw new InvalidDataException("Chiseled material data could not be verified: "+saved.Issue);
        byte[]? result=null;
        for(int i=0;i<saved.MaterialIds.Length;i++)
        {
            int source=saved.MaterialIds[i];
            if(!rules.TryGetValue(source,out int target)||!sourceMissing(source))continue;
            if(!targetAllowed(target)){skipped++;continue;}
            result??=original.ToArray();BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(saved.MaterialIdsOffset+i*4,4),target);changed++;
        }
        return result??original;
    }
}
