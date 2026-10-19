using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BlockLogger.WorldRepair;

// Dedicated connections and SQLite's online backup API give a consistent saved
// database copy while the official server keeps running. Never copy a live .db
// file with File.Copy; journal/WAL writes may be in flight.
internal static class MigrationWorldBackup
{
    internal static void Create(string sourcePath,string destination,CancellationToken token)
    {
        string partial=destination+".partial";
        try
        {
            token.ThrowIfCancellationRequested();
            if(File.Exists(destination))throw new IOException("Backup destination already exists.");
            DeletePartial(partial);
            using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=sourcePath,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString()))
            using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=partial,Pooling=false,DefaultTimeout=1}.ToString()))
            {
                source.Open();target.Open();
                using var backup=raw.sqlite3_backup_init(target.Handle!,"main",source.Handle!,"main");
                if(backup==null||backup.IsInvalid)throw new IOException("SQLite could not start the online world backup.");
                var elapsed=Stopwatch.StartNew();var busy=Stopwatch.StartNew();
                while(true)
                {
                    token.ThrowIfCancellationRequested();
                    if(elapsed.Elapsed>TimeSpan.FromHours(1))throw new TimeoutException("World backup exceeded one hour; migration remains inactive.");
                    int result=raw.sqlite3_backup_step(backup,128);
                    if(result==raw.SQLITE_DONE)break;
                    if(result==raw.SQLITE_OK)busy.Restart();
                    else if(result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
                    {if(busy.Elapsed>TimeSpan.FromSeconds(30))throw new IOException("World database remained busy during backup; migration remains inactive.");}
                    else throw new IOException("SQLite online world backup failed (code "+result+").");
                    Thread.Sleep(result==raw.SQLITE_OK?1:20);
                }
                int finish=raw.sqlite3_backup_finish(backup);if(finish!=raw.SQLITE_OK)throw new IOException("SQLite could not finish the world backup (code "+finish+").");
            }
            Verify(partial,token);token.ThrowIfCancellationRequested();File.Move(partial,destination);
        }
        finally{DeletePartial(partial);}
    }
    private static void DeletePartial(string path)
    {
        foreach(string suffix in new[]{"","-journal","-wal","-shm"})if(File.Exists(path+suffix))File.Delete(path+suffix);
    }
    internal static void Verify(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());connection.Open();
        using var cmd=connection.CreateCommand();cmd.CommandTimeout=600;cmd.CommandText="PRAGMA quick_check";using var registration=token.Register(cmd.Cancel);
        if(Convert.ToString(cmd.ExecuteScalar())!="ok")throw new InvalidDataException("Migration world backup failed integrity verification.");
    }
}
