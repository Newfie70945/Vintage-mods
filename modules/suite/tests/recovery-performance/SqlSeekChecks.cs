using System.Diagnostics;
using System.Text.RegularExpressions;
using BlockLogger.Services;
using Microsoft.Data.Sqlite;

internal static class SqlSeekChecks
{
    internal static async Task Run(RecoveryQueryRunner runner)
    {
        string? sourcePath=null;
        for(var folder=new DirectoryInfo(Environment.CurrentDirectory);folder!=null;folder=folder.Parent)
        {var file=Path.Combine(folder.FullName,"modules/suite/src/Services/LogStorageService.cs");if(File.Exists(file)){sourcePath=file;break;}}
        if(sourcePath==null)throw new Exception("Production recovery SQL source not found.");
        string source=File.ReadAllText(sourcePath),section=source[source.IndexOf("public Task<RollbackHistory> QueryRollbackRecoveryAsync",StringComparison.Ordinal)..];
        string Extract(string expression,string text)=>Regex.Match(text,expression,RegexOptions.Singleline).Groups[1].Value;
        string filePath=Path.Combine(Path.GetTempPath(),"nss-recovery-seek-"+Guid.NewGuid().ToString("N")+".db");
        string connect=new SqliteConnectionStringBuilder{DataSource=filePath,Pooling=false}.ToString();
        try
        {
            using(var db=new SqliteConnection(connect))
            {
                db.Open();
                void Exec(string sql){using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
                foreach(string table in new[]{"events","snapshots"})Exec(Regex.Match(source,@"CREATE TABLE IF NOT EXISTS "+table+@" \(.*?\);",RegexOptions.Singleline).Value);
                foreach(string index in new[]{"idx_events_world_dim_pos_time","idx_events_world_time","idx_events_player_uid_time","idx_events_player_name_time","idx_snapshots_world_dim_pos_time"})Exec(Regex.Match(source,@"CREATE INDEX IF NOT EXISTS "+index+@"\s.*?;",RegexOptions.Singleline).Value);
                Exec("INSERT INTO events(id,timestamp_ticks,timestamp_utc,action,player_uid,world_id,dimension,x,y,z,block_code,meta_json) VALUES(1,95,'',6,'owner','w','0',10,100,20,'game:chest','{}'),(2,120,'',5,'a','w','0',10,100,20,'game:chest','{}'),(3,140,'',5,'b','w','overworld',10,100,20,'game:chest','{}'),(4,350,'',2,'later','w','0',10,100,20,'game:chest','{}');");
                Exec("INSERT INTO snapshots(id,timestamp_ticks,timestamp_utc,world_id,dimension,x,y,z,block_code,slots_json) VALUES(1,90,'','w','overworld',10,100,20,'game:chest','[]'),(2,150,'','w','0',10,100,20,'game:chest','[]'),(3,400,'','w','overworld',10,100,20,'game:chest','[]');");
                using var transaction=db.BeginTransaction();using var insert=db.CreateCommand();insert.Transaction=transaction;
                insert.CommandText="INSERT INTO events(timestamp_ticks,timestamp_utc,action,player_uid,world_id,dimension,x,y,z,block_code,meta_json) VALUES(120,'',1,'other','w','0',$x,100,20,'game:stone','{}');";var x=insert.Parameters.AddWithValue("$x",0);
                for(int i=0;i<30000;i++){x.Value=i+1000;insert.ExecuteNonQuery();}transaction.Commit();
            }
            var elapsed=Stopwatch.StartNew();
            foreach(string name in new[]{"cmd","snapshots"})
            {
                string sql=Extract(name+@"\.CommandText\s*=\s*@""(.*?)"";",section);if(sql.Length==0)throw new Exception("Production query not extracted: "+name);
                var ids=await runner.RunAsync(()=>{var c=new SqliteConnection(connect);c.Open();return c;},(c,token)=>{
                    using var command=c.CreateCommand();command.CommandText=sql;
                    foreach(var pair in new Dictionary<string,object>{{"$world","w"},{"$uid",""},{"$area",1},{"$dimension","0"},{"$from",100L},{"$to",300L},{"$cx",10},{"$cy",100},{"$cz",20},{"$minX",10},{"$maxX",10},{"$minY",100},{"$maxY",100},{"$minZ",20},{"$maxZ",20},{"$r2",0}})command.Parameters.AddWithValue(pair.Key,pair.Value);
                    var values=new List<long>();using var reader=command.ExecuteReader();while(reader.Read()){token.ThrowIfCancellationRequested();values.Add(reader.GetInt64(reader.FieldCount-1));}return values.Order().ToArray();
                },CancellationToken.None,CancellationToken.None,TimeSpan.FromSeconds(10));
                if(!ids.SequenceEqual(name=="cmd"?new long[]{1,2,3,4}:new long[]{1,2,3}))throw new Exception("Native recovery SQL lost baseline/later-conflict rows: "+name);
            }
            Console.WriteLine($"PASS: production recovery SQL on Microsoft.Data.Sqlite/native SQLite, 30,000 unrelated events, exact cell/legacy dimensions and before/after conflict evidence ({elapsed.ElapsedMilliseconds} ms).");
        }
        finally{foreach(string suffix in new[]{"","-wal","-shm"})File.Delete(filePath+suffix);}
    }
}
