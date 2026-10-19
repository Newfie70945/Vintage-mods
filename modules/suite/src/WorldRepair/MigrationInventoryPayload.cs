using System.Text;

namespace BlockLogger.WorldRepair;

// Changes only the root block-code string and standard inventory slots. Unknown
// attributes elsewhere remain bytes, never constructed through mod classes.
internal static class MigrationInventoryPayload
{
    internal static byte[] Rewrite(byte[] original,SavedBlockEntityInspector.EntitySummary saved,string targetCode,byte[]? slots,bool overwrite)
    {
        if(!saved.Complete||saved.BlockCodeOffset<0||saved.SlotsOffset<0)throw new InvalidDataException("Compatible retained container metadata is required; no block entity will be invented.");
        if(slots!=null&&saved.NonEmptyStacks>0&&!overwrite)throw new InvalidOperationException("Recover-empty mode cannot replace existing contents.");
        using var text=new MemoryStream();using(var writer=new BinaryWriter(text,Encoding.UTF8,true))writer.Write(targetCode);
        var edits=new List<(int Start,int Length,byte[] Data)>{(saved.BlockCodeOffset,saved.BlockCodeLength,text.ToArray())};
        if(slots!=null)edits.Add((saved.SlotsOffset,saved.SlotsLength,slots));
        using var output=new MemoryStream();int cursor=0;
        foreach(var edit in edits.OrderBy(e=>e.Start))
        {
            if(edit.Start<cursor||edit.Length<0||edit.Start+edit.Length>original.Length)throw new InvalidDataException("Overlapping or invalid retained attribute ranges.");
            output.Write(original,cursor,edit.Start-cursor);output.Write(edit.Data);cursor=edit.Start+edit.Length;
        }
        output.Write(original,cursor,original.Length-cursor);var bytes=output.ToArray();var check=SavedBlockEntityInspector.Inspect(bytes);
        if(!check.Complete||check.Class!=saved.Class||check.X!=saved.X||check.InternalY!=saved.InternalY||check.Z!=saved.Z||check.InventorySlotCount!=saved.InventorySlotCount||check.SavedBlockCode!=targetCode)throw new InvalidDataException("Generated container metadata failed verification.");
        return bytes;
    }
}
