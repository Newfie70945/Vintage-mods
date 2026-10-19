using System.Reflection;
using System.Runtime.InteropServices;
using BlockLogger.WorldRepair;
using Microsoft.Data.Sqlite;
using System.Text.Json;

static class HistoryChecks
{
    public static void Run()
    {
        string game=Environment.GetEnvironmentVariable("VINTAGE_STORY")!;
        NativeLibrary.SetDllImportResolver(Assembly.Load("SQLitePCLRaw.provider.e_sqlite3"),(name,_,_)=>name=="e_sqlite3"?NativeLibrary.Load(Path.Combine(game,"Lib",OperatingSystem.IsLinux()?"libe_sqlite3.so":OperatingSystem.IsWindows()?"e_sqlite3.dll":"libe_sqlite3.dylib")):IntPtr.Zero);
        string path=Path.Combine(Path.GetTempPath(),"nss-history-"+Guid.NewGuid().ToString("N")+".db");int checks=0;
        void Assert(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        try
        {
            using var db=new SqliteConnection("Data Source="+path+";Pooling=false");db.Open();
            void Exec(string sql){using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
            Exec("CREATE TABLE events(id INTEGER PRIMARY KEY,timestamp_ticks INTEGER,block_code TEXT,action INTEGER,world_id TEXT,dimension TEXT,x INTEGER,y INTEGER,z INTEGER); CREATE INDEX idx_events_world_dim_pos_time ON events(world_id,dimension,x,y,z,timestamp_ticks DESC);CREATE TABLE snapshots(id INTEGER PRIMARY KEY,timestamp_ticks INTEGER,block_code TEXT,non_empty_slot_count INTEGER,total_item_count INTEGER,slot_count INTEGER,slots_json TEXT,world_id TEXT,dimension TEXT,x INTEGER,y INTEGER,z INTEGER);CREATE INDEX idx_snapshots_world_dim_pos_time ON snapshots(world_id,dimension,x,y,z,timestamp_ticks DESC)");
            DateTime start=new(2026,10,1,0,0,0,DateTimeKind.Utc);
            void Event(int seconds,string code,int action,string world="world-a",string dimension="0",int x=10)
            {using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO events(timestamp_ticks,block_code,action,world_id,dimension,x,y,z) VALUES($t,$c,$a,$w,$d,$x,20,30)";cmd.Parameters.AddWithValue("$t",start.AddSeconds(seconds).Ticks);cmd.Parameters.AddWithValue("$c",code);cmd.Parameters.AddWithValue("$a",action);cmd.Parameters.AddWithValue("$w",world);cmd.Parameters.AddWithValue("$d",dimension);cmd.Parameters.AddWithValue("$x",x);cmd.ExecuteNonQuery();}
            Event(0,"game:bookshelf",2);Event(1,"foreignworld",2,"other-world");Event(1,"foreigndim",2,dimension:"1");Event(1,"foreignpos",2,x:99);
            string slots=JsonSerializer.Serialize(new[]{new BlockHistoryAdvisor.SnapshotSlot{SlotId=0,ItemCode="game:book",StackSize=7}});
            using(var cmd=db.CreateCommand()){cmd.CommandText="INSERT INTO snapshots(timestamp_ticks,block_code,non_empty_slot_count,total_item_count,slot_count,slots_json,world_id,dimension,x,y,z) VALUES($t,'game:bookshelf',1,7,4,$json,'world-a','0',10,20,30)";cmd.Parameters.AddWithValue("$t",start.AddSeconds(1).Ticks);cmd.Parameters.AddWithValue("$json",slots);cmd.ExecuteNonQuery();}
            var targets=new Dictionary<string,BlockHistoryAdvisor.Target>{{"game:bookshelf",new(55,"Bookshelf")},{"game:rock-granite",new(42,"")}};
            BlockHistoryAdvisor.Report Read(int seconds){var r=new BlockHistoryAdvisor.Report{World="world-a",Dimension=0,X=10,Y=20,Z=30,BeforeUtc=start.AddSeconds(seconds)};BlockHistoryAdvisor.Read(path,r,targets,CancellationToken.None);return r;}
            var before=Read(2);Assert(before.EventsRead==1&&before.SnapshotsRead==1,"exact world/dimension/coordinate filters");Assert(before.Candidates.Single().Code=="game:bookshelf"&&!before.Candidates[0].Selectable,"bookshelf recommendation retains generic protection");Assert(before.InventorySnapshots[0].SlotsComplete&&before.InventorySnapshots[0].Slots[0].StackSize==7,"historical slot codes and quantities returned");
            Assert(InventoryRestoreHistory.LatestBefore(path,"world-a",10,20,30,0,start.AddSeconds(2)).Id==1,"latest snapshot lookup binds exact position and cutoff");
            var restore=InventoryRestoreHistory.Read(path,"world-a",10,20,30,0,1);Assert(restore.Slots[0].StackSize==7&&restore.Hash.Length==64,"exact snapshot selected for restoration");
            try{InventoryRestoreHistory.Read(path,"other-world",10,20,30,0,1);throw new Exception("Missing restore world binding");}catch(InvalidOperationException){checks++;}
            Exec("UPDATE snapshots SET timestamp_ticks="+DateTime.MaxValue.Ticks);
            try{InventoryRestoreHistory.Read(path,"world-a",10,20,30,0,1);throw new Exception("Missing future snapshot guard");}catch(InvalidDataException){checks++;}
            Exec("UPDATE snapshots SET timestamp_ticks="+start.AddSeconds(1).Ticks);
            Exec("UPDATE snapshots SET dimension='overworld'");
            Assert(InventoryRestoreHistory.Read(path,"world-a",10,20,30,0,1).Slots[0].StackSize==7,"legacy overworld snapshot matches dimension zero");
            Event(2,"game:bookshelf",5,dimension:"overworld");
            try{InventoryRestoreHistory.Read(path,"world-a",10,20,30,0,1);throw new Exception("Missing legacy-dimension mutation guard");}catch(InvalidOperationException){checks++;}
            Exec("DELETE FROM events WHERE action=5 AND dimension='overworld';UPDATE snapshots SET dimension='0'");
            Event(3,"game:bookshelf",1);
            try{InventoryRestoreHistory.Read(path,"world-a",10,20,30,0,1);throw new Exception("Missing later mutation guard");}catch(InvalidOperationException){checks++;}
            var broken=Read(4);Assert(broken.Candidates.All(c=>c.Confidence=="Low"),"break evidence never becomes strong replacement evidence");
            for(int i=0;i<100;i++)Event(2,"game:bookshelf",3);Event(5,"game:rock-granite",2);var changed=Read(6);Assert(changed.Candidates[0].Code=="game:rock-granite"&&changed.Candidates[0].Confidence=="Medium","latest placement outranks repeated older activity");Assert(changed.Candidates[1].Confidence=="Low","conflicting older code downgraded");
            for(int i=0;i<250;i++)Event(10+i,"game:rock-granite",3);var limited=Read(1000);Assert(limited.EventsRead==200&&limited.Limited&&limited.Candidates.All(c=>c.Confidence=="Low"),"bounded evidence window lowers confidence");
            Exec("UPDATE snapshots SET slots_json='not-json'");var bad=Read(2);Assert(!bad.InventorySnapshots[0].SlotsComplete&&bad.InventorySnapshots[0].Issue.Length>0,"invalid slot data is not treated as a recoverable inventory");
            Exec("DROP INDEX idx_events_world_dim_pos_time");try{Read(2);throw new Exception("Missing index should refuse an unbounded fallback");}catch(SqliteException){checks++;}
            Assert(ReadOnlyUnchanged(path),"query path uses no SQLite writes");
            Console.WriteLine($"{checks} BlockLogger recommendation/snapshot checks passed.");
        }
        finally{foreach(var suffix in new[]{"","-wal","-shm"})File.Delete(path+suffix);}
    }
    private static bool ReadOnlyUnchanged(string path)
    {
        byte[] before=File.ReadAllBytes(path);var report=new BlockHistoryAdvisor.Report{World="no-such-world",BeforeUtc=DateTime.UtcNow};
        try{BlockHistoryAdvisor.Read(path,report,new Dictionary<string,BlockHistoryAdvisor.Target>(),CancellationToken.None);}catch(SqliteException){}
        return before.SequenceEqual(File.ReadAllBytes(path));
    }
}
