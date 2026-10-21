using BlockLogger.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var folder=Path.Combine(Path.GetTempPath(),"nss-startup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
int count=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);count++;}
SqliteConnection Open(string path){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false,DefaultTimeout=1}.ToString());c.Open();return c;}
void Execute(SqliteConnection c,string sql){using var command=c.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
long Scalar(SqliteConnection c,string sql){using var command=c.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());}
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
try
{
 var path=Path.Combine(folder,"healthy.db");
 using(var c=Open(path))
 {
  LoggerDatabaseSchema.Ensure(c);Execute(c,"INSERT INTO events(timestamp_ticks,timestamp_utc,action,world_id,dimension,x,y,z,meta_json) VALUES(1,'',1,'world','0',1,2,3,'{}');");
  LoggerDatabaseSchema.Ensure(c);Check(Scalar(c,"SELECT count(*) FROM events")==1,"Schema startup lost records");
  Check(Scalar(c,"SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='idx_events_world_dim_pos_time'")==1,"Position index missing");
  var version=Scalar(c,"PRAGMA schema_version");LoggerDatabaseSchema.Ensure(c);Check(Scalar(c,"PRAGMA schema_version")==version,"Idempotent schema startup changed schema");
 }
 string hash=Hash(path);Check(LoggerDatabaseStartup.Probe(path,false).Contains("not scanned"),"Light probe claimed full integrity");Check(hash==Hash(path),"Read-only startup probe changed database");Check(LoggerDatabaseStartup.Probe(path,true)=="ok","Full startup probe failed on healthy DB");
 using(var locked=Open(path))
 {
  Execute(locked,"BEGIN EXCLUSIVE");bool failed=false;try{LoggerDatabaseStartup.Probe(path,false);}catch(SqliteException e)when(e.SqliteErrorCode is 5 or 6){failed=true;}finally{Execute(locked,"ROLLBACK");}
  Check(failed,"Exclusive lock was not surfaced as unavailable");Check(hash==Hash(path),"Busy startup altered/replaced DB");
 }
 var invalid=Path.Combine(folder,"corrupt.db");File.WriteAllBytes(invalid,"this is not a sqlite database"u8.ToArray());var invalidHash=Hash(invalid);bool corruptFailed=false;
 try{LoggerDatabaseStartup.Probe(invalid,false);}catch(SqliteException){corruptFailed=true;}
 Check(corruptFailed&&Hash(invalid)==invalidHash,"Invalid DB was not preserved");
 bool missingFailed=false;string missing=Path.Combine(folder,"absent.db");try{LoggerDatabaseStartup.Probe(missing,false);}catch(SqliteException){missingFailed=true;}Check(missingFailed&&!File.Exists(missing),"Read-only probe created missing DB");
 var badSchema=Path.Combine(folder,"bad-schema.db");using(var c=Open(badSchema))
 {
  Execute(c,"CREATE TABLE events(id INTEGER PRIMARY KEY, legacy_key TEXT);INSERT INTO events VALUES(1,'keep');");bool failed=false;try{LoggerDatabaseSchema.Ensure(c);}catch(SqliteException){failed=true;}
  Check(failed&&Scalar(c,"SELECT count(*) FROM events")==1,"Schema failure lost existing data");
  Check(Scalar(c,"SELECT count(*) FROM sqlite_schema WHERE name='meta'")==0,"Failed schema migration partially committed");
 }
 var legacy=Path.Combine(folder,"legacy.db");using(var c=Open(legacy))
 {
  Execute(c,"CREATE TABLE entity_activity(id INTEGER PRIMARY KEY,window_start_ticks INTEGER,window_end_ticks INTEGER,action INTEGER,entity_type_id INTEGER,entity_code TEXT,entity_class TEXT,event_count INTEGER,world_id TEXT,dimension TEXT,sample_x INTEGER,sample_y INTEGER,sample_z INTEGER);INSERT INTO entity_activity(id,window_end_ticks) VALUES(7,1);");
  LoggerDatabaseSchema.Ensure(c);Check(Scalar(c,"SELECT region_x+region_z FROM entity_activity WHERE id=7")==0,"Legacy schema region defaults failed");
 }
 Check(LoggerDatabaseStartup.DeferAutomaticBackup(false,TimeSpan.FromHours(1)),"Backup allowed before RunGame");
 Check(LoggerDatabaseStartup.DeferAutomaticBackup(true,TimeSpan.FromMilliseconds(119999)),"Backup allowed during warmup");
 Check(!LoggerDatabaseStartup.DeferAutomaticBackup(true,TimeSpan.FromMinutes(2)),"Backup remained blocked after warmup");
 Console.WriteLine($"PASS {count} database startup, failure preservation, transactional schema and backup-warmup checks");
 if(args.Length>0&&args[0]=="--benchmark")
 {
  string output=Path.GetFullPath(args[1]);if(Directory.Exists(output))throw new Exception("Benchmark output already exists");Directory.CreateDirectory(output);
  string benchmark=Path.Combine(output,"benchmark.db");int rows=args.Length>2?int.Parse(args[2]):100000;
  using(var c=Open(benchmark))
  {
   Execute(c,"PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;");LoggerDatabaseSchema.Ensure(c);
   using var tx=c.BeginTransaction();using var insert=c.CreateCommand();insert.Transaction=tx;
   insert.CommandText="INSERT INTO events(timestamp_ticks,timestamp_utc,action,world_id,dimension,x,y,z,player_uid,player_name,block_code,meta_json) VALUES($i,'2026-10-20T00:00:00Z',1,'fixture','0',$x,65,$z,$uid,$name,'game:rock-granite',$meta)";
   insert.Parameters.AddWithValue("$i",0);insert.Parameters.AddWithValue("$x",0);insert.Parameters.AddWithValue("$z",0);insert.Parameters.AddWithValue("$uid","");insert.Parameters.AddWithValue("$name","");insert.Parameters.AddWithValue("$meta","{\"fixture\":\""+new string('x',2048)+"\"}");
   for(int i=0;i<rows;i++){insert.Parameters["$i"].Value=i;insert.Parameters["$x"].Value=i%1024;insert.Parameters["$z"].Value=i/1024;insert.Parameters["$uid"].Value="uid"+(i%40);insert.Parameters["$name"].Value="Player"+(i%40);insert.ExecuteNonQuery();}tx.Commit();
   Execute(c,"PRAGMA wal_checkpoint(TRUNCATE)");
  }
  var samples=new List<object>();
  for(int iteration=0;iteration<6;iteration++)foreach(bool full in iteration%2==0?new[]{true,false}:new[]{false,true})
  {
   long alloc=GC.GetAllocatedBytesForCurrentThread();var cpu=Process.GetCurrentProcess().TotalProcessorTime;var watch=Stopwatch.StartNew();string result=LoggerDatabaseStartup.Probe(benchmark,full);watch.Stop();
   samples.Add(new{iteration,mode=full?"full":"lightweight",elapsedMs=watch.Elapsed.TotalMilliseconds,cpuMs=(Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds,managedAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-alloc,result});
  }
  var report=new{rows,databaseBytes=new FileInfo(benchmark).Length,synthetic=true,claimScope="Component startup probe only; no production startup/TPS claim",samples};
  File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("Benchmark written to "+output);
 }
}
finally{SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
