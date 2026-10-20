using BlockLogger.Models;
using BlockLogger.Services;

// Links the production planner directly. No game binaries or third-party packages are needed.
internal static class Program
{
    static readonly DateTime From=new(2026,10,4,20,0,0,DateTimeKind.Utc);
    static readonly DateTime To=From.AddHours(1);
    static long next=1;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static LogEventRecord Block(int minute,string who,string before,string after,LogActionType action=LogActionType.BlockPlace)=>new(){StorageId=next++,TimestampUtc=From.AddMinutes(minute),PlayerUid=who,Dimension="0",X=10,Y=100,Z=10,Action=action,BlockCode=action==LogActionType.BlockBreak?before:after,Meta=new(){["oldBlockCode"]=before,["newBlockCode"]=after}};
    static LogEventRecord Delta(int minute,int quantity,LogActionType action)=>new(){StorageId=next++,TimestampUtc=From.AddMinutes(minute),PlayerUid="attacker",Dimension="0",X=10,Y=100,Z=10,Action=action,Meta=new(){["slotId"]="0",["quantity"]=quantity.ToString(),["itemCode"]="game:ingot-copper"}};
    static ContainerSnapshotRecord Snapshot(int minute,int quantity)=>new(){StorageId=next++,TimestampUtc=From.AddMinutes(minute),PlayerUid="attacker",Dimension="0",X=10,Y=100,Z=10,BlockCode="game:chest-east",SlotCount=16,NonEmptySlotCount=quantity==0?0:1,TotalItemCount=quantity,Slots=quantity==0?new():new(){new(){SlotId=0,ItemCode="game:ingot-copper",StackSize=quantity}}};
    static RollbackHistory History(params LogEventRecord[] events){var h=new RollbackHistory{PlayerUid="attacker",WorldId="world"};h.Events.AddRange(events);return h;}
    static RecoveryBuild Build(RollbackHistory h,bool inventory=false,bool entity=false)=>RollbackRecoveryPlanner.Build(h,From,To,inventory,entity);
    static void Main()
    {
        var h=History(Block(10,"attacker","game:stone","air",LogActionType.BlockBreak),Block(20,"attacker","air","game:dirt"),Block(30,"attacker","game:dirt","air",LogActionType.BlockBreak));
        var op=Build(h).Operations.Single();Check(op.Before=="game:stone"&&op.After=="air","Multi-step destruction must return the original block");
        h.Events.Add(Block(40,"other","air","game:wood"));Check(Build(h).Operations.Count==0,"Later legitimate edit must exclude location");
        h=History(Block(5,"other","air","game:stone"),Block(10,"attacker","game:stone","air",LogActionType.BlockBreak));Check(Build(h).Operations.Count==1,"Earlier other-player edit must not prevent restoring their block");
        h=History(Block(10,"attacker","game:stone","air",LogActionType.BlockBreak),Block(70,"attacker","air","game:dirt"));Check(Build(h).Operations.Count==0,"Post-window attacker activity is a conflict");
        h=History(Delta(10,5,LogActionType.ContainerTake));h.Snapshots.Add(Snapshot(5,10));h.Snapshots.Add(Snapshot(11,5));
        op=Build(h,true).Operations.Single();Check(op.Kind=="inventory"&&op.InventoryBefore!.Single().Count==10&&op.InventoryAfter!.Single().Count==5,"Recover stolen quantities from baseline plus independently recorded final state");
        h.Snapshots[1].Slots[0].StackSize=4;h.Snapshots[1].TotalItemCount=4;Check(Build(h,true).Operations.Count==0,"Snapshot/delta mismatch must not overwrite inventory");
        h=History(Delta(10,5,LogActionType.ContainerTake),Block(20,"attacker","game:chest-east","air",LogActionType.BlockBreak));h.Snapshots.Add(Snapshot(5,10));h.Snapshots.Add(Snapshot(11,5));
        op=Build(h,true).Operations.Single();Check(op.InventoryBefore!.Single().Count==10&&op.Before=="game:chest-east","Destroyed container can use existing baseline contents");
        h=History(Delta(10,5,LogActionType.ContainerTake));h.Snapshots.Add(Snapshot(-10,10));h.Snapshots.Add(Snapshot(11,5));op=Build(h,true).Operations.Single();Check(op.InventoryBefore!.Single().Count==10,"Baseline may precede selected attack window");
        h.Events.Add(new(){StorageId=next++,TimestampUtc=From.AddMinutes(15),PlayerUid="other",Dimension="0",X=10,Y=100,Z=10,Action=LogActionType.ContainerPut,Meta=new(){["slotId"]="0",["quantity"]="1",["itemCode"]="game:ingot-copper"}});Check(Build(h,true).Operations.Count==0,"Other player's later inventory activity must be preserved");
        h=History(Delta(10,5,LogActionType.ContainerTake));h.Snapshots.Add(Snapshot(5,10));Check(Build(h,true).Operations.Count==0,"Existing container requires a recorded final inventory, not baseline alone");
        var legacy=Block(10,"attacker","game:stone","air",LogActionType.BlockBreak);legacy.Meta.Clear();h=History(legacy);Check(Build(h).Operations.Single().Before=="game:stone","Legacy breaks can recover logged removed type");
        legacy=Block(10,"attacker","air","game:stone");legacy.Meta.Clear();Check(Build(History(legacy)).Operations.Count==0,"Unknown legacy overwritten block cannot be inferred");
        h=History(Block(10,"attacker","game:stone","air",LogActionType.BlockBreak));h.Events.Add(new(){StorageId=next++,TimestampUtc=From.AddMinutes(40),PlayerUid="admin",Dimension="0",X=10,Y=100,Z=10,Action=LogActionType.BlockUse,Meta=new(){["rollbackToken"]="old"}});Check(Build(h).Operations.Count==0,"Previous rollback barrier must stop overlapping recovery");
        h=History(new LogEventRecord(){StorageId=next++,TimestampUtc=From.AddMinutes(15),PlayerUid="attacker",Dimension="0",Action=LogActionType.EntityDeath,Meta=new(){["entityId"]="123",["entityCode"]="game:chicken-hen",["entityClass"]="EntityAgent"}});
        Check(Build(h).Operations.Count==0&&Build(h,false,true).Operations.Single().Kind=="entity-replacement","Creature replacement is explicit and never labelled exact resurrection");
        h.Events[0].Meta["entityClass"]="EntityPlayer";Check(Build(h,false,true).Operations.Count==0,"Players cannot be recreated");
        h=History(Block(10,"attacker","game:stone","air",LogActionType.BlockBreak));var a=Build(h).Operations.Single();var b=Build(h).Operations.Single();Check(a.ReceiptKey==b.ReceiptKey&&a.Fingerprint==b.Fingerprint,"Repeated preview must produce stable receipts");
        // Preserve legitimate inventory changes between an older baseline and the selected attack window.
        h=History(Delta(-5,2,LogActionType.ContainerTake),Delta(10,5,LogActionType.ContainerTake));h.Events[0].PlayerUid="owner";
        h.Snapshots.Add(Snapshot(-10,10));h.Snapshots.Add(Snapshot(11,3));op=Build(h,true).Operations.Single();Check(op.InventoryBefore!.Single().Count==8,"Pre-window legitimate removal must remain removed");
        // An unchanged disconnect snapshot after the end is evidence, not a later mutation.
        h=History(Delta(10,5,LogActionType.ContainerTake));h.Snapshots.Add(Snapshot(5,10));h.Snapshots.Add(Snapshot(70,5));Check(Build(h,true).Operations.Count==1,"Later consistent final snapshot should remain usable");
        h.Snapshots[1].Slots[0].StackSize=6;h.Snapshots[1].TotalItemCount=6;Check(Build(h,true).Operations.Count==0,"Late snapshot mismatch indicates unrecorded changes");
        h=History(Block(10,"attacker","game:stone","air",LogActionType.BlockBreak),Block(10,"attacker","air","game:dirt"));op=Build(h).Operations.Single();Check(op.Before=="game:stone"&&op.After=="game:dirt","Same-timestamp block transitions need stable ID ordering");
        h.WorldId="second-world";Check(Build(h).Operations.Single().ReceiptKey!=a.ReceiptKey,"Recovery receipts must be world scoped");
        h=History(Block(10,"attacker","game:chest-east","air",LogActionType.BlockBreak),Block(20,"attacker","air","game:chest-east"));h.Snapshots.Add(Snapshot(5,10));h.Snapshots.Add(Snapshot(21,10));
        Check(Build(h,true).Operations.Single().Kind=="inventory","Net-zero container block chain must still inspect contents");
        h.Snapshots.RemoveAt(1);Check(Build(h,true).Operations.Count==0,"Replaced container needs independently recorded final state");
        h=History(Delta(10,5,LogActionType.ContainerTake));h.Snapshots.Add(Snapshot(5,10));h.Snapshots.Add(Snapshot(11,5));h.Snapshots[0].InventoryId="original";h.Snapshots[1].InventoryId="replacement";Check(Build(h,true).Operations.Count==0,"Replacement inventory identity must not be overwritten");
        h.Snapshots[1].InventoryId="original";h.Events[0].Meta["inventoryId"]="different";Check(Build(h,true).Operations.Count==0,"Deltas from another inventory cannot reconstruct this container");
        h=History(new LogEventRecord(){StorageId=next++,TimestampUtc=From.AddMinutes(-1),PlayerUid="attacker",Dimension="0",Action=LogActionType.EntityDeath,Meta=new(){["entityId"]="123",["entityCode"]="game:chicken-hen",["entityClass"]="EntityAgent"}});Check(Build(h,false,true).Operations.Count==0,"Pre-window deaths cannot be replaced");
        // Area mode combines changes from every actor, preserving post-window activity.
        h=History(Block(10,"alice","game:stone","air",LogActionType.BlockBreak),Block(20,"bob","air","game:dirt"));h.AreaMode=true;h.PlayerUid="";
        op=Build(h).Operations.Single();Check(op.Before=="game:stone"&&op.After=="game:dirt"&&op.SourceIds.Count==2,"Area must reverse a mixed-actor chain together");
        h.Events.Add(Block(70,"charlie","game:dirt","game:wood"));Check(Build(h).Operations.Count==0,"Area must preserve post-window third-party edits");
        h=History(Delta(10,2,LogActionType.ContainerTake),Delta(20,3,LogActionType.ContainerTake));h.Events[0].PlayerUid="alice";h.Events[1].PlayerUid="bob";h.AreaMode=true;h.PlayerUid="";
        h.Snapshots.Add(Snapshot(5,10));h.Snapshots[0].PlayerUid="owner";h.Snapshots.Add(Snapshot(21,5));h.Snapshots[1].PlayerUid="bob";
        op=Build(h,true).Operations.Single();Check(op.InventoryBefore!.Single().Count==10&&op.InventoryAfter!.Single().Count==5,"Area inventory deltas can come from multiple actors");
        h.Snapshots.Add(Snapshot(70,5));Check(Build(h,true).Operations.Count==0,"Area post-window container snapshot is a conservative conflict");
        h=History(Block(10,"","game:stone","air",LogActionType.BlockBreak));h.AreaMode=true;h.PlayerUid="";Check(Build(h).Operations.Count==1,"Area must include empty-actor block records");
        var areaOp=Build(h).Operations.Single();h.AreaMode=false;h.PlayerUid="";Check(Build(h).Operations.Single().ReceiptKey==areaOp.ReceiptKey,"Same source operation shares receipts across scopes");
        h=History(new LogEventRecord(){StorageId=next++,TimestampUtc=From.AddMinutes(15),Dimension="0",Action=LogActionType.EntityDeath,Meta=new(){["entityId"]="777",["entityCode"]="game:chicken-hen",["entityClass"]="EntityAgent"}});h.AreaMode=true;h.PlayerUid="unrelated";Check(Build(h,false,true).Operations.Single().Kind=="entity-replacement","Area explicit creature option includes natural deaths");
        h=History(Block(10,"alice","game:stone","air",LogActionType.BlockBreak));h.AreaMode=true;h.Events.Add(new(){StorageId=next++,TimestampUtc=From.AddMinutes(20),Dimension="0",X=10,Y=100,Z=10,Action=LogActionType.BlockUse,Meta=new(){["rollbackToken"]="prior"}});Check(Build(h).Operations.Count==0,"Area still respects earlier recovery barriers");
        using(var cancelled=new CancellationTokenSource())
        {cancelled.Cancel();try{RollbackRecoveryPlanner.Build(h,From,To,true,true,cancelled.Token);throw new Exception("Cancelled planning returned operations");}catch(OperationCanceledException){ }}
        var cell=new RecoveryCell("0",10,100,10);
        h=History(Delta(10,5,LogActionType.ContainerTake));h.AreaMode=true;h.Events[0].BlockCode="game:chest-east";h.Events[0].Meta["inventoryId"]="chest-one";
        var live=new RecoveryLiveInventory(cell,"game:chest-east","chest-one",16,new(){new(0,"game:ingot-copper",5)});
        RecoveryBuild Live()=>RollbackRecoveryPlanner.Build(h,From,To,true,false,default,live);
        Check(Build(h,true).Operations.Count==0,"Snapshot-free rollback must remain explicit");
        op=Live().Operations.Single();Check(op.InventoryBefore!.Single().Count==10&&op.InventoryAfter!.Single().Count==5,"Explicit live anchor reverses missing-snapshot take logs");
        h.Events[0].Meta["inventoryId"]="other";Check(Live().Operations.Count==0,"Wrong inventory cannot use live anchor");h.Events[0].Meta["inventoryId"]="chest-one";
        h.Events[0].Action=LogActionType.ContainerPut;h.Events[0].Meta["quantity"]="6";Check(Live().Operations.Count==0,"Impossible inverse count must fail");
        h.Events[0].Action=LogActionType.ContainerTake;h.Events[0].Meta["quantity"]="5";h.Events.Add(Block(70,"other","game:chest-east","air"));Check(Live().Operations.Count==0,"Post-window replacement blocks live-anchor recovery");
        h=History(new LogEventRecord(){StorageId=90000,TimestampUtc=From.AddMinutes(10),Action=LogActionType.BlockUse,BlockCode="game:stone",Dimension="0",X=10,Y=100,Z=10});h.AreaMode=true;
        op=RollbackRecoveryPlanner.Build(h,From,To,true,false,default,null,new(cell,999999)).Operations.Single();Check(op.Before=="game:stone"&&op.MissingBlockId==999999,"Missing numeric block can use unambiguous same-location use history");
        h.Events.Add(new(){StorageId=90001,TimestampUtc=From.AddMinutes(20),Action=LogActionType.BlockUse,BlockCode="game:dirt",Dimension="0",X=10,Y=100,Z=10});Check(RollbackRecoveryPlanner.Build(h,From,To,true,false,default,null,new(cell,999999)).Operations.Count==0,"Ambiguous block identity cannot be guessed");
        Console.WriteLine("PASS: production planner recovery/conflict/legacy/snapshot/entity cases.");
    }
}

namespace Newtonsoft.Json
{
    // Models only use this metadata attribute; the planner test needs no JSON runtime.
    [AttributeUsage(AttributeTargets.Property)] public sealed class JsonIgnoreAttribute : Attribute { }
}
