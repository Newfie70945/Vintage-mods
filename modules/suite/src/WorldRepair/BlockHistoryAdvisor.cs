using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BlockLogger.WorldRepair;

// Exact-position, indexed, bounded evidence only. Historical activity never
// authorizes a whole-world ID replacement or reconstructs an inventory.
public static class BlockHistoryAdvisor
{
    public sealed record Target(int Id,string EntityClass);
    public sealed record Evidence(long RecordId,string Source,string Action,string Code,DateTime TimestampUtc,int? RecordedItems=null,int? RecordedOccupiedSlots=null);
    public sealed class Candidate
    {
        public string Code { get; set; } = "";
        public int? CurrentId { get; set; }
        public string EntityClass { get; set; } = "";
        public bool Selectable { get; set; }
        public string Confidence { get; set; } = "Low";
        public string Reason { get; set; } = "";
        public int SupportingRecords { get; set; }
        public List<Evidence> Evidence { get; set; } = [];
    }
    public sealed class SnapshotSlot
    {
        public int SlotId { get; set; }
        public string ItemCode { get; set; } = "";
        public int StackSize { get; set; }
    }
    public sealed class InventorySnapshot
    {
        public long RecordId { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string BlockCode { get; set; } = "";
        public int SlotCount { get; set; }
        public int? RecordedItems { get; set; }
        public int? RecordedOccupiedSlots { get; set; }
        public bool SlotsComplete { get; set; }
        public string Issue { get; set; } = "";
        public List<SnapshotSlot> Slots { get; set; } = [];
    }
    public sealed class Report
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string State { get; set; } = "running";
        public string World { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int Dimension { get; set; }
        public DateTime BeforeUtc { get; set; }
        public int EventsRead { get; set; }
        public int SnapshotsRead { get; set; }
        public bool Limited { get; set; }
        public List<Candidate> Candidates { get; set; } = [];
        public List<InventorySnapshot> InventorySnapshots { get; set; } = [];
        public string InventoryLimits { get; set; } = "Historical BlockLogger snapshots record slot number, item code and quantity only. They do not store complete item attributes such as durability, written text, perish timers or contents of bags. Up to three snapshot inventories are shown. Later transfers/changes are not reconciled, so copying a snapshot can duplicate or overwrite items. No automatic inventory restoration is performed.";
        public string Message { get; set; } = "";
        public string Scope { get; set; } = "Evidence applies to this coordinate, world and dimension only. A global ID repair changes every saved occurrence, so confirm the other occurrences separately. Confidence is advisory, never a guarantee. Break records describe removed blocks. Logs can be incomplete, pruned, queued or affected by an earlier bad mapping. Snapshot item counts are historical, not proof those items should be restored now. No inventory contents or terrain are changed.";
    }
    public static void Read(string path,Report report,IReadOnlyDictionary<string,Target> targets,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!File.Exists(path)){report.State="unavailable";report.Message="No BlockLogger database is available for this world. No recommendation was inferred.";return;}
        var evidence=new List<Evidence>();
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString()))
        {
            db.Open();
            foreach(bool snapshots in new[]{false,true})
            {
                token.ThrowIfCancellationRequested();using var cmd=db.CreateCommand();cmd.CommandTimeout=2;
                // Force the matching existing index. An old/missing index produces a
                // diagnostic instead of falling back to a scan of a multi-GB database.
                cmd.CommandText=snapshots?"SELECT id,timestamp_ticks,substr(block_code,1,257),non_empty_slot_count,total_item_count,slot_count,length(slots_json),substr(slots_json,1,65537) FROM snapshots INDEXED BY idx_snapshots_world_dim_pos_time WHERE world_id=$world AND dimension IN ($dim,$legacy) AND x=$x AND y=$y AND z=$z AND timestamp_ticks<=$before ORDER BY timestamp_ticks DESC LIMIT 51":
                    "SELECT id,timestamp_ticks,substr(block_code,1,257),action FROM events INDEXED BY idx_events_world_dim_pos_time WHERE world_id=$world AND dimension IN ($dim,$legacy) AND x=$x AND y=$y AND z=$z AND timestamp_ticks<=$before ORDER BY timestamp_ticks DESC LIMIT 201";
                cmd.Parameters.AddWithValue("$world",report.World);cmd.Parameters.AddWithValue("$dim",report.Dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$legacy",report.Dimension==0?"overworld":report.Dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$x",report.X);cmd.Parameters.AddWithValue("$y",report.Y);cmd.Parameters.AddWithValue("$z",report.Z);cmd.Parameters.AddWithValue("$before",report.BeforeUtc.Ticks);
                using var reader=cmd.ExecuteReader();int rows=0,limit=snapshots?50:200;
                while(reader.Read())
                {
                    token.ThrowIfCancellationRequested();if(++rows>limit){report.Limited=true;break;}
                    if(snapshots)report.SnapshotsRead++;else report.EventsRead++;
                    long ticks=reader.GetInt64(1);if(ticks<0||ticks>DateTime.MaxValue.Ticks)continue;
                    string code=reader.IsDBNull(2)?"":reader.GetString(2);if(code.Length==0||code.Length>256||code.Any(char.IsWhiteSpace))continue;if(!code.Contains(':'))code="game:"+code;
                    int action=snapshots?0:reader.GetInt32(3);string name=snapshots?"Snapshot":action switch{1=>"Break",2=>"Place",3=>"Use",4=>"ContainerOpen",5=>"ContainerTake",6=>"ContainerPut",_=>""};if(name.Length==0)continue;
                    if(snapshots&&report.InventorySnapshots.Count<3)
                    {
                        var snapshot=new InventorySnapshot{RecordId=reader.GetInt64(0),TimestampUtc=new DateTime(ticks,DateTimeKind.Utc),BlockCode=code,SlotCount=reader.IsDBNull(5)?0:reader.GetInt32(5),RecordedItems=reader.IsDBNull(4)?null:reader.GetInt32(4),RecordedOccupiedSlots=reader.IsDBNull(3)?null:reader.GetInt32(3)};
                        try
                        {
                            if(reader.IsDBNull(6)||reader.GetInt64(6)>65536||snapshot.SlotCount<0||snapshot.SlotCount>4096)throw new InvalidDataException("Snapshot exceeds bounds or lacks a valid slot count.");
                            var slots=JsonSerializer.Deserialize<List<SnapshotSlot>>(reader.GetString(7),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,MaxDepth=8})??throw new InvalidDataException("No saved slot array.");
                            if(slots.Count>4096||slots.Any(s=>s==null||s.SlotId<0||s.SlotId>=snapshot.SlotCount||s.StackSize<=0||string.IsNullOrWhiteSpace(s.ItemCode)||s.ItemCode.Length>256)||slots.Select(s=>s.SlotId).Distinct().Count()!=slots.Count||slots.Count!=snapshot.RecordedOccupiedSlots||slots.Sum(s=>(long)s.StackSize)!=snapshot.RecordedItems)throw new InvalidDataException("Snapshot slot data and recorded totals disagree.");
                            snapshot.Slots=slots.Take(128).ToList();snapshot.SlotsComplete=slots.Count<=128;if(!snapshot.SlotsComplete)snapshot.Issue="Only the first 128 occupied slots are displayed.";
                        }catch(Exception e)when(e is JsonException or InvalidDataException or OverflowException){snapshot.Issue=e.Message;}
                        report.InventorySnapshots.Add(snapshot);
                    }
                    evidence.Add(new Evidence(reader.GetInt64(0),snapshots?"snapshots":"events",name,code,new DateTime(ticks,DateTimeKind.Utc),snapshots&&!reader.IsDBNull(4)?reader.GetInt32(4):null,snapshots&&!reader.IsDBNull(3)?reader.GetInt32(3):null));
                }
            }
        }
        report.Candidates=Rank(evidence,targets).Take(10).ToList();if(report.Limited)foreach(var candidate in report.Candidates){candidate.Confidence="Low";candidate.Reason="History window reached its row limit; evidence is incomplete. "+candidate.Reason;}report.State="complete";
        report.Message=report.Candidates.Count==0?"No usable exact-position block history found within the selected cutoff. World/dimension/coordinates must match; legacy overworld records are included as dimension 0.":"Suggestions from exact-position history. Review evidence and cutoff before choosing a destination; nothing has been staged.";
    }
    public static IEnumerable<Candidate> Rank(IEnumerable<Evidence> records,IReadOnlyDictionary<string,Target> targets)
    {
        var history=records.OrderByDescending(e=>e.TimestampUtc).ThenByDescending(e=>e.RecordId).ToArray();
        var latestChange=history.FirstOrDefault(e=>e.Action is "Place" or "Break");
        var latestObserved=history.FirstOrDefault(e=>e.Action!="Break");
        return history.GroupBy(e=>e.Code,StringComparer.Ordinal).Select(group=>{
            targets.TryGetValue(group.Key,out var target);
            bool latestPlace=latestChange?.Action=="Place"&&latestChange.Code==group.Key;
            bool latestObservation=latestObserved?.Code==group.Key;
            bool removed=latestChange?.Action=="Break"&&latestChange.Code==group.Key;
            bool breaksOnly=group.All(e=>e.Action=="Break");
            var candidate=new Candidate{Code=group.Key,CurrentId=target?.Id,EntityClass=target?.EntityClass??"",Selectable=target!=null&&string.IsNullOrEmpty(target.EntityClass),SupportingRecords=group.Count(),Evidence=group.Take(5).ToList(),Confidence=latestObservation&&(latestPlace||latestChange==null)&&!breaksOnly?"Medium":"Low"};
            candidate.Reason=breaksOnly?"Only removal evidence: this block was broken, not confirmed as the current/original replacement.":removed?"The latest recorded block change is a break. Later observations may be incomplete; verify manually.":latestPlace&&latestObservation?"Matches the latest recorded placement and observation at this location. Missing or post-incident logs can still mislead.":latestObservation?"Latest observed block here, but placement/removal history does not establish a safe replacement.":"Older or conflicting block history at this location; not the latest observed identity.";
            if(target==null)candidate.Reason+=" Code is not currently registered; restore its mod or verify a compatible exact variant.";
            else if(!candidate.Selectable)candidate.Reason+=" Block entity requires dedicated recovery; generic replacement is blocked.";
            return candidate;
        }).OrderByDescending(c=>c.Confidence=="Medium").ThenByDescending(c=>c.Evidence[0].TimestampUtc);
    }
}
