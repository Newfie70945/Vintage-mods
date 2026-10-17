using BlockLogger.Commands;using BlockLogger.Services;using BlockLogger.Models;
using Vintagestory.API.Server;using Vintagestory.API.Common;using Vintagestory.API.Common.Entities;using Vintagestory.API.MathTools;
using System.Text.Json;
static class Program {
 static void Check(bool b,string m){if(!b)throw new Exception(m);}
 static void Main(){
 var temp=Path.Combine(Path.GetTempPath(),"recovery-command-"+Guid.NewGuid());Directory.CreateDirectory(temp);
 try {
 var api=new Api();var admin=new Player("admin",new(10,100,10,0));api.World.AllOnlinePlayers=new[]{admin};var storage=new LogStorageService{DatabasePath=Path.Combine(temp,"history.db")};
 using var cmd=new PlayerRollbackCommands(api,storage,null);cmd.Register();
 bool Busy()=>(bool)typeof(PlayerRollbackCommands).GetField("busy",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(cmd)!;
 void PumpUntil(Func<bool> done){var timeout=System.Diagnostics.Stopwatch.StartNew();while(!done()){if(timeout.Elapsed.TotalSeconds>10)throw new Exception("Recovery worker did not finish");if(api.Event.Main.TryDequeue(out var action))action();else if(api.Event.Callbacks.Count>0)api.Event.Callbacks.Dequeue()(0);else Thread.Sleep(1);}}
 PumpUntil(()=>(bool)typeof(PlayerRollbackCommands).GetField("receiptReady",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(cmd)!);
 void Run(string text){api.Handler!(admin,0,new CmdArgs(text));PumpUntil(()=>!Busy()&&api.Event.Main.IsEmpty);}
 int gameThread=Environment.CurrentManagedThreadId;
 api.World.BlockAccessor.BeforeWrite=()=>{
  Check(Environment.CurrentManagedThreadId==gameThread,"World mutation escaped the game thread");
  string receipts=storage.DatabasePath+".recovery-receipts.jsonl";
  Check(File.Exists(receipts)&&File.ReadAllText(receipts).Contains("intent"),"Mutation preceded the durable receipt");
 };
 string Token()=>Directory.GetFiles(temp,"*.recovery-preview-*.json").Select(f=>(File.GetLastWriteTimeUtc(f),f)).OrderByDescending(x=>x.Item1).Select(x=>JsonDocument.Parse(File.ReadAllText(x.f)).RootElement.GetProperty("token").GetString()!).First();
 foreach(var text in new[]{"preview area 0 2h","preview area 4097 2h","preview area 10 8d","preview area 10 from 2026-10-04T21:00:00Z to 2026-10-04T20:00:00Z","preview area 10 2h radius 20","preview area 10 2h garbage"})Run(text);
 Check(storage.Queries.Count==0,"Invalid area request reached storage");
 Run("preview area 20 2h --inventories");var query=storage.Queries.Single();Check(query.Target==""&&query.Center==new RecoveryCell("0",10,100,10)&&query.Radius==20&&query.To-query.From==TimeSpan.FromHours(2),"Area radius/center/time was not forwarded");Check(api.World.BlockAccessor.Writes==0,"Preview changed world");var token=Token();
 admin.Entity.Position=new(1000,100,1000,0);var visitor=new Player("visitor",new(30,100,10,0));api.World.AllOnlinePlayers=new[]{admin,visitor};Run("execute "+token);Check(storage.Queries.Count==1&&admin.Messages.Last().Contains("leave"),"Boundary occupant must block execution at saved center");
 visitor.Entity.Position=new(10,100,10,1);Run("execute "+token);Check(storage.Queries.Count==2&&storage.Queries.Last().Center==query.Center,"Execution moved center or rejected player in other dimension");
 Run("preview area 10 from 2026-10-04T20:00:00Z to 2026-10-04T20:30:00Z --replace-entities");Check(storage.Queries.Last().To-storage.Queries.Last().From==TimeSpan.FromMinutes(30),"Absolute area time window not parsed");Run("cancel");Run("execute "+Token());Check(admin.Messages.Last().Contains("No matching"),"Cancelled preview was executable");
 Run("preview player attacker 2h radius 10");Check(storage.Queries.Last().Target=="attacker"&&storage.Queries.Last().Center==null,"Player radius accidentally enabled all-actor query");Run("cancel");
 // Run real command batching against a fake live world. Mutation remains the real adapter code.
 admin.Entity.Position=new(10,100,10,0);api.World.AllOnlinePlayers=new[]{admin};storage.History.Events.Clear();
 for(int i=0;i<17;i++)storage.History.Events.Add(new(){StorageId=100+i,TimestampUtc=DateTime.UtcNow.AddMinutes(-10),PlayerUid=i%2==0?"alice":"bob",Dimension="0",X=10+i,Y=100,Z=10,Action=LogActionType.BlockBreak,BlockCode="game:stone",Meta=new(){["oldBlockCode"]="game:stone",["newBlockCode"]="air"}});
 Run("preview area 50 2h");token=Token();api.Handler!(admin,0,new CmdArgs("execute "+token));
 PumpUntil(()=>api.World.BlockAccessor.Writes==1);
 visitor.Entity.Position=new(10,100,10,0);api.World.AllOnlinePlayers=new[]{admin,visitor};PumpUntil(()=>!Busy());Check(api.World.BlockAccessor.Writes==1&&admin.Messages.Last().Contains("entered"),"Entrant must stop later recovery dispatches");
 // A live edit while the worker flushes its intent must be rechecked before mutation.
 api.World.AllOnlinePlayers=new[]{admin};storage.History.Events.Clear();storage.History.Events.Add(new(){StorageId=999,TimestampUtc=DateTime.UtcNow.AddMinutes(-10),PlayerUid="alice",Dimension="0",X=29,Y=100,Z=10,Action=LogActionType.BlockBreak,BlockCode="game:stone",Meta=new(){["oldBlockCode"]="game:stone",["newBlockCode"]="air"}});
 Run("preview area 50 2h");token=Token();api.Handler!(admin,0,new CmdArgs("execute "+token));
 PumpUntil(()=>!api.Event.Main.IsEmpty);Check(api.Event.Main.TryDequeue(out var prepare),"Prepare callback missing");prepare!();
 api.World.BlockAccessor.Blocks["0:29:100:10"]=1;PumpUntil(()=>!Busy());Check(api.World.BlockAccessor.Writes==1,"Live change during durable write was overwritten");
 // Durable receipt failure must leave the world unchanged.
 api.World.BlockAccessor.Blocks["0:29:100:10"]=0;storage.History.Events[0].StorageId=1000;
 Run("preview area 50 2h");token=Token();
 using(var lockedReceipt=new FileStream(storage.DatabasePath+".recovery-receipts.jsonl",FileMode.Open,FileAccess.ReadWrite,FileShare.None))Run("execute "+token);
 Check(api.World.BlockAccessor.Writes==1&&admin.Messages.Last().Contains("stopped"),"Failed journal write mutated world");
 // Explicit cancellation stops pending execution before its first mutation.
 storage.History.Events[0].StorageId=1001;Run("preview area 50 2h");token=Token();api.Handler!(admin,0,new CmdArgs("execute "+token));api.Handler!(admin,0,new CmdArgs("cancel"));PumpUntil(()=>!Busy());Check(api.World.BlockAccessor.Writes==1,"Cancelled operation mutated world");
 api.Handler!(admin,0,new CmdArgs("preview area 50 2h"));api.Handler!(admin,0,new CmdArgs("cancel"));PumpUntil(()=>!Busy());Check(api.World.BlockAccessor.Writes==1,"Cancelled preview mutated world");
 // A complete operation must close receipt handles before the next is admitted.
 storage.History.Events[0].StorageId=1002;Run("preview area 50 2h");Run("execute "+Token());Check(api.World.BlockAccessor.Writes==2,"Successful asynchronous recovery did not complete");
 api.World.BlockAccessor.Blocks["0:29:100:10"]=0;storage.History.Events[0].StorageId=1003;Run("preview area 50 2h");Run("execute "+Token());Check(api.World.BlockAccessor.Writes==3,"Previous completion retained the receipt writer handle");
 // Claimed land needs an explicit flag; permissions are rechecked after preview.
 api.World.BlockAccessor.Blocks["0:29:100:10"]=0;storage.History.Events[0].StorageId=1004;api.World.Claims.Claimed=true;
 Run("preview area 50 2h");Run("execute "+Token());Check(api.World.BlockAccessor.Writes==3,"Default recovery overwrote claimed land");
 Run("preview area 50 2h --include-claims");token=Token();api.World.Claims.Deny=true;Run("execute "+token);Check(api.World.BlockAccessor.Writes==3,"Claim permission changed after preview but recovery still mutated");
 api.World.Claims.Deny=false;storage.History.Events[0].StorageId=1005;Run("preview area 50 2h --include-claims");Run("execute "+Token());Check(api.World.BlockAccessor.Writes==4,"Explicit authorized claim recovery did not execute");
 Console.WriteLine("PASS: claimed land excluded by default; explicit claim recovery checks current access and rejects permission changes after preview.");
 Console.WriteLine("PASS: production recovery command grammar, locked center/time, player isolation, preview non-mutation, cancellation, area occupancy, mixed-actor execution, durable-before-mutation ordering, live-change revalidation, journal failures and cancellation (API/storage doubles).");
 }finally{Directory.Delete(temp,true);}
 }
 sealed class Player:IServerPlayer {public string PlayerUID{get;}public string PlayerName=>PlayerUID;public Entity Entity{get;}public List<string> Messages=new();public Player(string uid,BlockPos p){PlayerUID=uid;Entity=new(){Position=p};}public void SendMessage(int g,string t,EnumChatType ty)=>Messages.Add(t);}
 sealed class Api:ICoreServerAPI {public FakeWorld World{get;}=new();public FakeEvents Event{get;}=new();public FakePermissions Permissions{get;}=new();public FakeLogger Logger{get;}=new();public CommandHandler? Handler;public void RegisterCommand(string n,string d,string h,CommandHandler c,string priv)=>Handler=c;}
}
