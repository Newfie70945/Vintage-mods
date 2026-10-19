using Microsoft.Data.Sqlite;

namespace BlockLogger.WorldRepair;

// Disk-backed prepared payloads keep history queries and unbounded recovery
// dictionaries out of the chunk loader. Receipts survive reloads and new plans.
internal static class MigrationInventoryStore
{
    internal sealed record Entry(long Chunk,int X,int Y,int Z,int Dimension,int Source,int Target,string EntityHash,byte[] Payload,long Items,string SnapshotHash,long SnapshotTicks);
    internal static SqliteConnection Connect(string path,bool readOnly)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,Pooling=false,DefaultTimeout=0}.ToString());c.Open();return c;
    }
    internal static void Create(string path,string identity)
    {
        if(File.Exists(path))throw new InvalidOperationException("Inventory preparation already exists; create a new review.");
        using var c=Connect(path,false);using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.CommandText="PRAGMA journal_mode=DELETE; CREATE TABLE metadata(identity TEXT NOT NULL); INSERT INTO metadata VALUES($identity); CREATE TABLE entries(chunk INTEGER NOT NULL,x INTEGER NOT NULL,y INTEGER NOT NULL,z INTEGER NOT NULL,dimension INTEGER NOT NULL,source INTEGER NOT NULL,target INTEGER NOT NULL,entity_hash TEXT NOT NULL,payload BLOB NOT NULL,items INTEGER NOT NULL,snapshot_hash TEXT NOT NULL,snapshot_ticks INTEGER NOT NULL,PRIMARY KEY(chunk,x,y,z,dimension));";cmd.Parameters.AddWithValue("$identity",identity);cmd.ExecuteNonQuery();
    }
    internal static void Add(SqliteConnection c,Entry e,SqliteTransaction? tx=null)
    {
        if(e.Payload.Length>2*1024*1024)throw new InvalidDataException("Prepared container exceeds the 2 MiB limit.");
        using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.Transaction=tx;cmd.CommandText="INSERT INTO entries VALUES($c,$x,$y,$z,$d,$s,$t,$h,$p,$i,$sh,$st)";
        foreach(var p in new (string,object)[]{("$c",e.Chunk),("$x",e.X),("$y",e.Y),("$z",e.Z),("$d",e.Dimension),("$s",e.Source),("$t",e.Target),("$h",e.EntityHash),("$p",e.Payload),("$i",e.Items),("$sh",e.SnapshotHash),("$st",e.SnapshotTicks)})cmd.Parameters.AddWithValue(p.Item1,p.Item2);cmd.ExecuteNonQuery();
    }
    internal static void Verify(string path,string identity)
    {
        using var c=Connect(path,true);using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.CommandText="SELECT identity FROM metadata";
        if(Convert.ToString(cmd.ExecuteScalar())!=identity)throw new InvalidDataException("Prepared inventory file belongs to another review/world/registry.");
    }
    internal static List<Entry> ReadChunk(string path,long chunk)
    {
        using var c=Connect(path,true);using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.CommandText="SELECT x,y,z,dimension,source,target,entity_hash,length(payload),payload,items,snapshot_hash,snapshot_ticks FROM entries WHERE chunk=$c LIMIT 257";cmd.Parameters.AddWithValue("$c",chunk);
        using var r=cmd.ExecuteReader();var result=new List<Entry>();long total=0;
        while(r.Read())
        {
            long size=r.GetInt64(7);if(result.Count>=256||size>2*1024*1024||(total+=size)>8*1024*1024)throw new InvalidDataException("Prepared chunk inventories exceed verification bounds.");
            result.Add(new(chunk,r.GetInt32(0),r.GetInt32(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetString(6),(byte[])r[8],r.GetInt64(9),r.GetString(10),r.GetInt64(11)));
        }
        return result;
    }
    internal static void EnsureReceipts(string path)
    {
        using var c=Connect(path,false);using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.CommandText="PRAGMA journal_mode=DELETE; CREATE TABLE IF NOT EXISTS receipts(world TEXT NOT NULL,x INTEGER NOT NULL,y INTEGER NOT NULL,z INTEGER NOT NULL,dimension INTEGER NOT NULL,intent_ticks INTEGER NOT NULL,snapshot_hash TEXT NOT NULL,plan TEXT NOT NULL,PRIMARY KEY(world,x,y,z,dimension));";cmd.ExecuteNonQuery();
    }
    internal static void CheckReceipt(SqliteConnection c,SqliteTransaction? tx,string world,Entry e)
    {
        if(e.SnapshotHash.Length==0)return;
        using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.Transaction=tx;cmd.CommandText="SELECT intent_ticks,snapshot_hash FROM receipts WHERE world=$w AND x=$x AND y=$y AND z=$z AND dimension=$d";Bind(cmd,world,e);
        using var r=cmd.ExecuteReader();if(r.Read()&&(e.SnapshotTicks<=r.GetInt64(0)||e.SnapshotHash==r.GetString(1)))throw new InvalidOperationException("Inventory recovery at this location already has an intent receipt. Older snapshots cannot be replayed; inspect the previous recovery/backup.");
    }
    private static void Bind(SqliteCommand cmd,string world,Entry e)
    {
        cmd.Parameters.AddWithValue("$w",world);cmd.Parameters.AddWithValue("$x",e.X);cmd.Parameters.AddWithValue("$y",e.Y);cmd.Parameters.AddWithValue("$z",e.Z);cmd.Parameters.AddWithValue("$d",e.Dimension);
    }
    internal static void Reserve(string path,string world,string plan,IReadOnlyList<Entry> entries)
    {
        using var c=Connect(path,false);using var sync=c.CreateCommand();sync.CommandTimeout=1;sync.CommandText="PRAGMA synchronous=FULL";sync.ExecuteNonQuery();using var tx=c.BeginTransaction();
        foreach(var e in entries.Where(e=>e.SnapshotHash.Length>0))
        {
            CheckReceipt(c,tx,world,e);using var cmd=c.CreateCommand();cmd.CommandTimeout=1;cmd.Transaction=tx;cmd.CommandText="INSERT INTO receipts VALUES($w,$x,$y,$z,$d,$t,$h,$p) ON CONFLICT(world,x,y,z,dimension) DO UPDATE SET intent_ticks=excluded.intent_ticks,snapshot_hash=excluded.snapshot_hash,plan=excluded.plan";Bind(cmd,world,e);cmd.Parameters.AddWithValue("$t",DateTime.UtcNow.Ticks);cmd.Parameters.AddWithValue("$h",e.SnapshotHash);cmd.Parameters.AddWithValue("$p",plan);cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
