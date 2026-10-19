using BlockLogger.WorldRepair;
using Microsoft.Data.Sqlite;

static class MigrationBackupChecks
{
    public static void Run()
    {
        int checks=0;void Assert(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        string folder=Path.Combine(Path.GetTempPath(),"nss-online-backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string source=Path.Combine(folder,"source.db"),backup=Path.Combine(folder,"backup.db"),cancelled=Path.Combine(folder,"cancelled.db");
        try
        {
            using(var db=new SqliteConnection("Data Source="+source+";Pooling=false"))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL;CREATE TABLE paired(id INTEGER PRIMARY KEY,value INTEGER);INSERT INTO paired VALUES(1,0),(2,0);CREATE TABLE padding(value BLOB);";cmd.ExecuteNonQuery();
                using var tx=db.BeginTransaction();cmd.Transaction=tx;cmd.CommandText="INSERT INTO padding VALUES(zeroblob(32768))";for(int i=0;i<256;i++)cmd.ExecuteNonQuery();tx.Commit();
            }
            File.WriteAllText(backup+".partial-journal","Interrupted private backup fixture");
            var writer=Task.Run(()=>
            {
                using var db=new SqliteConnection("Data Source="+source+";Pooling=false");db.Open();
                for(int i=1;i<=10;i++){using var cmd=db.CreateCommand();cmd.CommandText="UPDATE paired SET value=$v";cmd.Parameters.AddWithValue("$v",i);cmd.ExecuteNonQuery();Thread.Sleep(10);}
            });
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10)))MigrationWorldBackup.Create(source,backup,timeout.Token);writer.GetAwaiter().GetResult();
            using(var db=new SqliteConnection("Data Source="+backup+";Mode=ReadOnly;Pooling=false"))
            {
                db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(DISTINCT value) FROM paired";Assert(Convert.ToInt32(cmd.ExecuteScalar())==1,"online backup mixed transactions");
                cmd.CommandText="SELECT COUNT(*) FROM padding";Assert(Convert.ToInt32(cmd.ExecuteScalar())==256,"online backup lost pages");
                cmd.CommandText="PRAGMA integrity_check";Assert(Convert.ToString(cmd.ExecuteScalar())=="ok","online backup corrupt");
            }
            Assert(!File.Exists(backup+".partial")&&!File.Exists(backup+".partial-journal"),"successful backup left partial file/journal");
            try{MigrationWorldBackup.Create(source,backup,CancellationToken.None);throw new Exception("backup overwritten");}catch(IOException){checks++;}
            using(var cancellation=new CancellationTokenSource(5))
            {
                try{MigrationWorldBackup.Create(source,cancelled,cancellation.Token);throw new Exception("cancelled backup completed");}catch(OperationCanceledException){checks++;}
            }
            Assert(!File.Exists(cancelled)&&!File.Exists(cancelled+".partial"),"cancelled backup left a usable/partial backup");
            MigrationWorldBackup.Verify(backup,CancellationToken.None);checks++;
        }
        finally{Directory.Delete(folder,true);}
        Console.WriteLine($"{checks} live-world backup, concurrent writer, integrity and cancellation checks passed.");
    }
}
