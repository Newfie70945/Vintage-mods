using BlockLogger.WorldRepair;
internal static class MigrationSkipChecks
{
    internal static void Run()
    {
        var h=new MigrationSkipHistory();
        h.Record(1,2,3,0,9,10,"reason");h.Record(1,2,3,0,9,10,"reason");
        if(h.Snapshot().Length!=1||h.Snapshot()[0].Attempts!=2)throw new Exception("Skip attempts not aggregated");
        var saved=h.Snapshot();for(int i=0;i<40;i++)h.Record(i,0,0,1,9,10,new string('x',1000));
        if(h.Snapshot().Length!=32||h.Snapshot()[0].X!=39||h.Snapshot()[0].Reason.Length!=300||saved[0].Attempts!=2)throw new Exception("Skip history is not bounded, ordered and snapshot isolated");
        h.Record(39,0,0,2,9,10,"reason");if(h.Snapshot()[0].Dimension!=2||h.Snapshot()[0].Attempts!=1)throw new Exception("Skip dimensions mixed");
        Console.WriteLine("PASS bounded migration skip diagnostics");
    }
}
