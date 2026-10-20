namespace BlockLogger.WorldRepair;

// Bounded diagnostic samples, never chunk/entity references. Not an audit/undo log.
internal sealed class MigrationSkipHistory
{
    internal sealed record Entry(int X,int Y,int Z,int Dimension,int Source,int Target,string Reason,long Attempts);
    private readonly List<Entry> entries=[];
    internal void Record(int x,int y,int z,int dimension,int source,int target,string reason)
    {
        reason=reason.Length>300?reason[..300]:reason;
        int index=entries.FindIndex(e=>e.X==x&&e.Y==y&&e.Z==z&&e.Dimension==dimension&&e.Source==source&&e.Target==target&&e.Reason==reason);
        long attempts=1;
        if(index>=0){attempts=entries[index].Attempts+1;entries.RemoveAt(index);}
        entries.Add(new(x,y,z,dimension,source,target,reason,attempts));
        if(entries.Count>32)entries.RemoveAt(0);
    }
    internal Entry[] Snapshot()=>entries.AsEnumerable().Reverse().ToArray();
}
