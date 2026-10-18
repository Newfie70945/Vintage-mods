using BlockLogger.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
var runner=new RecoveryQueryRunner();
SqliteConnection Open(){var connection=new SqliteConnection("Data Source=:memory:");connection.Open();return connection;}
long LongQuery(SqliteConnection connection,CancellationToken token){using var command=connection.CreateCommand();command.CommandText="WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<100000000) SELECT sum(n) FROM numbers;";return (long)command.ExecuteScalar()!;}
using var cancel=new CancellationTokenSource();
var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var first=runner.RunAsync(Open,(connection,token)=>{started.SetResult();return LongQuery(connection,token);},CancellationToken.None,cancel.Token);
if(await Task.WhenAny(started.Task,first)==first)await first;
await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
try{await runner.RunAsync(Open,LongQuery,CancellationToken.None,CancellationToken.None);throw new Exception("Concurrent recovery query was admitted");}catch(InvalidOperationException){ }
var elapsed=Stopwatch.StartNew();cancel.Cancel();
try{await first.WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("Cancelled native query completed normally");}catch(OperationCanceledException){ }
Check(elapsed.Elapsed<TimeSpan.FromSeconds(5),"Native interrupt did not promptly stop SQLite");
long interruptionMs=elapsed.ElapsedMilliseconds;
try{await runner.RunAsync(Open,LongQuery,CancellationToken.None,CancellationToken.None,TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("Deadline did not stop native query");}catch(TimeoutException ex){Check(ex.Message.Contains("deadline"),"Wait timeout rather than query deadline");}
using var race=new CancellationTokenSource();
try{await runner.RunAsync(Open,(connection,token)=>{race.Cancel();return LongQuery(connection,token);},CancellationToken.None,race.Token).WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("Pre-statement cancellation race was missed");}catch(OperationCanceledException){ }
long recovered=await runner.RunAsync(Open,(connection,token)=>{using var command=connection.CreateCommand();command.CommandText="SELECT 42";return (long)command.ExecuteScalar()!;},CancellationToken.None,CancellationToken.None);
Check(recovered==42,"Query gate was not released after cancellation/deadline");
using var stopped=new CancellationTokenSource();stopped.Cancel();
try{await runner.RunAsync(Open,LongQuery,stopped.Token,CancellationToken.None);throw new Exception("Shutdown admitted a query");}catch(OperationCanceledException){ }
Console.WriteLine($"PASS: native SQLite interruption ({interruptionMs} ms), one-reader admission, deadline, deterministic pre-statement cancellation race, post-cancellation retry and shutdown rejection.");
await SqlSeekChecks.Run(runner);
