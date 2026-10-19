using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BlockLogger.WorldRepair;

public static class InventoryRestoreHistory
{
    public sealed class Source
    {
        public long Id {get;set;}
        public string Code {get;set;}="";
        public DateTime TimestampUtc {get;set;}
        public int SlotCount {get;set;}
        public List<BlockHistoryAdvisor.SnapshotSlot> Slots {get;set;}=[];
        public string Hash=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{Id,Code,TimestampUtc,SlotCount,Slots}))));
    }
    public static Source LatestBefore(string path,string world,int x,int y,int z,int dimension,DateTime before)
    {
        if(!File.Exists(path))throw new InvalidOperationException("BlockLogger history is unavailable.");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandTimeout=2;
        cmd.CommandText="SELECT id FROM snapshots INDEXED BY idx_snapshots_world_dim_pos_time WHERE world_id=$world AND dimension IN ($dim,$legacy) AND x=$x AND y=$y AND z=$z AND timestamp_ticks<=$before ORDER BY timestamp_ticks DESC,id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$world",world);cmd.Parameters.AddWithValue("$dim",dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$legacy",dimension==0?"overworld":dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$x",x);cmd.Parameters.AddWithValue("$y",y);cmd.Parameters.AddWithValue("$z",z);cmd.Parameters.AddWithValue("$before",before.Ticks);
        object? id=cmd.ExecuteScalar();if(id==null)throw new InvalidOperationException("No inventory snapshot exists at/before the selected cutoff for this exact location.");
        // Never skip a newer empty/invalid/mismatched snapshot to resurrect an
        // older non-empty one. Read also rejects later logged block/item changes.
        return Read(path,world,x,y,z,dimension,Convert.ToInt64(id));
    }
    public static Source Read(string path,string world,int x,int y,int z,int dimension,long id)
    {
        if(id<=0||!File.Exists(path))throw new InvalidOperationException("Choose a retained BlockLogger snapshot for this exact location.");
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandTimeout=2;cmd.CommandText="SELECT timestamp_ticks,block_code,slot_count,non_empty_slot_count,total_item_count,length(slots_json),substr(slots_json,1,65537) FROM snapshots WHERE id=$id AND world_id=$world AND dimension IN ($dim,$legacy) AND x=$x AND y=$y AND z=$z";
        void Bind(SqliteCommand c){c.Parameters.AddWithValue("$world",world);c.Parameters.AddWithValue("$dim",dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));c.Parameters.AddWithValue("$legacy",dimension==0?"overworld":dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));c.Parameters.AddWithValue("$x",x);c.Parameters.AddWithValue("$y",y);c.Parameters.AddWithValue("$z",z);}
        Bind(cmd);cmd.Parameters.AddWithValue("$id",id);Source source;
        using(var row=cmd.ExecuteReader()){
            if(!row.Read())throw new InvalidOperationException("Snapshot is absent or belongs to a different world, dimension or coordinate.");
            if(row.IsDBNull(5)||row.GetInt64(5)>65536)throw new InvalidDataException("Snapshot slot data is missing or oversized.");
            source=new Source{Id=id,TimestampUtc=new DateTime(row.GetInt64(0),DateTimeKind.Utc),Code=row.GetString(1),SlotCount=row.GetInt32(2),Slots=JsonSerializer.Deserialize<List<BlockHistoryAdvisor.SnapshotSlot>>(row.GetString(6),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,MaxDepth=8})??throw new InvalidDataException("Missing snapshot slots.")};
            if(source.TimestampUtc>DateTime.UtcNow)throw new InvalidDataException("Snapshot timestamp is in the future; automatic restoration is refused.");
            if(!source.Code.Contains(':'))source.Code="game:"+source.Code;
            if(source.SlotCount<1||source.SlotCount>64||source.Slots.Count==0||source.Slots.Count>64||source.Slots.Any(s=>s==null||s.SlotId<0||s.SlotId>=source.SlotCount||s.StackSize<=0||string.IsNullOrWhiteSpace(s.ItemCode)||s.ItemCode.Length>256)||source.Slots.Select(s=>s.SlotId).Distinct().Count()!=source.Slots.Count||source.Slots.Count!=row.GetInt32(3)||source.Slots.Sum(s=>(long)s.StackSize)!=row.GetInt64(4))throw new InvalidDataException("Snapshot counts or slots are invalid, empty or unsupported.");
        }
        using var later=db.CreateCommand();later.CommandTimeout=2;Bind(later);later.Parameters.AddWithValue("$time",source.TimestampUtc.Ticks);
        later.CommandText="SELECT action FROM events INDEXED BY idx_events_world_dim_pos_time WHERE world_id=$world AND dimension IN ($dim,$legacy) AND x=$x AND y=$y AND z=$z AND timestamp_ticks >= $time ORDER BY timestamp_ticks DESC LIMIT 201";
        using var rows=later.ExecuteReader();int count=0;while(rows.Read()){if(++count>200)throw new InvalidOperationException("Later history exceeds the safety window; no inventory restoration inferred.");if(rows.GetInt32(0) is 1 or 2 or 5 or 6)throw new InvalidOperationException("A block change or item transfer was logged at/after this snapshot. Automatic restoration is refused to avoid duplication.");}
        return source;
    }
}
