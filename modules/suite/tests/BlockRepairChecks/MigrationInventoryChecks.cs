using System.Text;
using BlockLogger.WorldRepair;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

static class MigrationInventoryChecks
{
    public static void Run()
    {
        int count=0;void Assert(bool value,string why){if(!value)throw new Exception(why);count++;}
        void Refuse(Action run,string why){try{run();throw new Exception(why);}catch(Exception e)when(e is InvalidOperationException or InvalidDataException or ArgumentException){count++;}}
        new MigrationInventoryOptions().Validate();count++;
        Refuse(()=>new MigrationInventoryOptions("recover-empty").Validate(),"missing risk acknowledgement accepted");
        Refuse(()=>new MigrationInventoryOptions("replace",null,true,false).Validate(),"missing overwrite acknowledgement accepted");
        Refuse(()=>MigrationInventoryOptions.Parse("recover-empty","not a date",true,false),"invalid cutoff accepted");
        Refuse(()=>new MigrationInventoryOptions("recover-empty",DateTime.UtcNow.AddDays(1),true).Validate(),"future cutoff accepted");
        Assert(MigrationInventoryOptions.Parse("recover-empty","2026-10-01T12:00:00-02:30",true,false).BeforeUtc==new DateTime(2026,10,1,14,30,0,DateTimeKind.Utc),"cutoff normalized to UTC");
        var original=Checks.InventoryPayload();var saved=SavedBlockEntityInspector.Inspect(original);
        var retained=MigrationInventoryPayload.Rewrite(original,saved,"game:bookshelf",null,false);var preserved=SavedBlockEntityInspector.Inspect(retained);
        Assert(preserved.NonEmptyStacks==1&&preserved.SerializedItemQuantity==7&&preserved.SavedBlockCode=="game:bookshelf","compatible container remap preserves contents");
        Assert(retained.AsSpan(preserved.SlotsOffset,preserved.SlotsLength).SequenceEqual(original.AsSpan(saved.SlotsOffset,saved.SlotsLength)),"retained inventory and written item attributes remain byte exact");
        var slots=new TreeAttribute();slots.SetItemstack("0",new ItemStack{Class=EnumItemClass.Item,Id=99,StackSize=1});
        Refuse(()=>MigrationInventoryPayload.Rewrite(original,saved,"game:bookshelf",slots.ToBytes(),false),"recover-empty replaced existing contents");
        var replacement=MigrationInventoryPayload.Rewrite(original,saved,"game:bookshelf",slots.ToBytes(),true);var replaced=SavedBlockEntityInspector.Inspect(replacement);
        Assert(replaced.Stacks.Single().Id==99&&replaced.SerializedItemQuantity==1,"explicit overwrite replaces exactly the reviewed slots");
        Assert(original.AsSpan(saved.SlotsOffset+saved.SlotsLength).SequenceEqual(replacement.AsSpan(replaced.SlotsOffset+replaced.SlotsLength)),"non-inventory suffix remains unchanged");
        var empty=Checks.InventoryPayload(empty:true);var emptySummary=SavedBlockEntityInspector.Inspect(empty);var recovered=MigrationInventoryPayload.Rewrite(empty,emptySummary,"game:bookshelf",slots.ToBytes(),false);
        Assert(SavedBlockEntityInspector.Inspect(recovered).NonEmptyStacks==1,"empty compatible retained inventory can be filled");
        Refuse(()=>MigrationInventoryPayload.Rewrite(empty[..^1],SavedBlockEntityInspector.Inspect(empty[..^1]),"game:bookshelf",slots.ToBytes(),false),"truncated retained data accepted");
        string folder=Path.Combine(Path.GetTempPath(),"nss-migration-inv-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            string manifest=Path.Combine(folder,"prepared.db"),receipts=Path.Combine(folder,"receipts.db");MigrationInventoryStore.Create(manifest,"identity-a");MigrationInventoryStore.EnsureReceipts(receipts);
            var entry=new MigrationInventoryStore.Entry(123,17,0,0,0,999999,55,saved.PayloadSha256,recovered,1,"snapshot-a",DateTime.UtcNow.AddDays(-1).Ticks);
            using(var c=MigrationInventoryStore.Connect(manifest,false))MigrationInventoryStore.Add(c,entry);
            MigrationInventoryStore.Verify(manifest,"identity-a");count++;Refuse(()=>MigrationInventoryStore.Verify(manifest,"other-world"),"manifest identity not bound");
            var rows=MigrationInventoryStore.ReadChunk(manifest,123);Assert(rows.Count==1&&rows[0].Payload.SequenceEqual(recovered)&&MigrationInventoryStore.ReadChunk(manifest,124).Count==0,"prepared payloads are chunk-bound and byte exact");
            using(var c=MigrationInventoryStore.Connect(receipts,true))MigrationInventoryStore.CheckReceipt(c,null,"world-a",entry);count++;
            MigrationInventoryStore.Reserve(receipts,"world-a","plan-a",[entry]);count++;
            Refuse(()=>MigrationInventoryStore.Reserve(receipts,"world-a","plan-a",[entry]),"same recovery replayed");
            Refuse(()=>MigrationInventoryStore.Reserve(receipts,"world-a","new-plan",[entry with{SnapshotHash="different-old"}]),"old snapshot replayed through another plan");
            MigrationInventoryStore.Reserve(receipts,"world-b","plan-a",[entry]);count++;
            var other=entry with{X=18,SnapshotHash="snapshot-b"};Refuse(()=>MigrationInventoryStore.Reserve(receipts,"world-a","atomic",[other,entry]),"reservation should reject whole group");
            using(var c=MigrationInventoryStore.Connect(receipts,true))MigrationInventoryStore.CheckReceipt(c,null,"world-a",other);count++;
            MigrationInventoryStore.Reserve(receipts,"world-a","retained-only",[entry with{SnapshotHash=""}]);count++;
        }
        finally{Directory.Delete(folder,true);}
        Console.WriteLine($"{count} migration inventory payload, options, manifest and replay-receipt checks passed.");
    }
}
