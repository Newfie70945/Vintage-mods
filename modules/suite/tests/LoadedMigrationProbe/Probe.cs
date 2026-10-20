using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using BlockLogger;
using BlockLogger.WorldRepair;
using Vintagestory.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

// Disposable integration server only. Requests an existing fixture column through
// the official loader; no authenticated clients or movement packets are simulated.
public sealed class LoadedMigrationProbe:ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        var p=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseheldmigrationprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("X"),p.Int("Y"),p.Int("Z")).HandleWith(a=>
        {
            var repair=(BlockIdRepairService)typeof(BlockLoggerModSystem).GetField("blockRepair",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(api.ModLoader.GetModSystem<BlockLoggerModSystem>())!;
            var player=(ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));var data=(ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));
            const string uid="held-migration-test-administrator";typeof(ServerWorldPlayerData).GetField("PlayerUID",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(data,uid);
            var pos=new BlockPos((int)a[0],(int)a[1],(int)a[2]);data.EntityPlayer=new EntityPlayer{BlockSelection=new(){Position=pos}};
            typeof(ServerPlayer).GetField("worlddata",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,data);
            var inventory=new InventoryGeneric(11,"hotbar-"+uid,api);var block=api.World.GetBlock(new AssetLocation("game:rock-granite"));inventory[0].Itemstack=new ItemStack(block,7);
            var inventories=new Vintagestory.API.Datastructures.OrderedDictionary<string,InventoryBase>();inventories.Add("hotbar-"+uid,inventory);
            var manager=new ServerPlayerInventoryManager(inventories,player,(ServerMain)api.World);typeof(ServerPlayer).GetField("inventoryMgr",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,manager);
            int before=api.World.BlockAccessor.GetBlockId(pos);
            string message=(string)typeof(BlockIdRepairService).GetMethod("ReplaceLookedAtHeld",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[player])!;
            int after=api.World.BlockAccessor.GetBlockId(pos);
            var ruleList=(TextCommandResult)typeof(BlockIdRepairService).GetMethod("ListMigrationRules",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[1])!;
            bool rulesShown=ruleList.StatusMessage.Contains("999997")&&ruleList.StatusMessage.Contains("game:rock-granite");if(!rulesShown)throw new Exception("Saved held rule was not visible in the in-game rule list.");
            if(before!=999997||after!=before||inventory[0].StackSize!=7)throw new Exception("Held rule changed terrain immediately or consumed held stack.");
            // The same adapter must reject a healthy looked-at source.
            data.EntityPlayer.BlockSelection=new(){Position=pos.AddCopy(-2,0,0)};bool healthyRejected=false;
            try{typeof(BlockIdRepairService).GetMethod("ReplaceLookedAtHeld",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[player]);}
            catch(TargetInvocationException e)when(e.InnerException?.Message.Contains("protected")==true){healthyRejected=true;}
            if(!healthyRejected)throw new Exception("Healthy looked-at source was not protected.");
            api.Logger.Notification("{0}","NSE_HELD_MIGRATION_PROBE "+JsonSerializer.Serialize(new{sourceId=before,heldQuantity=inventory[0].StackSize,healthyRejected,rulesShown,message}));
            return TextCommandResult.Success("Held migration adapter checked using detached administrator fixture.");
        });
        api.ChatCommands.Create("nsemigrationcancelprobe").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>
        {
            var repair=(BlockIdRepairService)typeof(BlockLoggerModSystem).GetField("blockRepair",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(api.ModLoader.GetModSystem<BlockLoggerModSystem>())!;
            repair.ConfigureMigrationRule(999997,"game:rock-granite",new(),false,true);
            var work=(System.Threading.Tasks.Task)typeof(BlockIdRepairService).GetField("liveActivationWorker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(repair)!;
            if(!work.Wait(TimeSpan.FromSeconds(5)))throw new Exception("Activation did not enqueue its callback.");
            repair.DisableLoadedMigration();
            api.Event.RegisterCallback(dt=>
            {
                var snapshot=JsonSerializer.SerializeToElement(repair.Snapshot());var loaded=snapshot.GetProperty("loadedMigration");
                if(loaded.GetProperty("active").GetBoolean()||loaded.GetProperty("Enabled").GetBoolean())throw new Exception("Stale activation re-enabled disabled migration.");
                api.Logger.Notification("{0}","NSE_LIVE_CANCEL_PROBE "+JsonSerializer.Serialize(new{disabled=true,queuedActivationCouldNotReactivate=true}));
            },300);
            return TextCommandResult.Success("Queued activation cancelled before its server-thread callback.");
        });
        api.ChatCommands.Create("nseunloadfixture").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("chunk X"),p.Int("chunk Z")).HandleWith(a=>
        {api.WorldManager.UnloadChunkColumn((int)a[0],(int)a[1]);api.Logger.Notification("NSE_FIXTURE_UNLOADED");return TextCommandResult.Success("Saved fixture column unloaded.");});
        api.ChatCommands.Create("nsemigrationprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("X"),p.Int("Y"),p.Int("Z")).HandleWith(a=>
        {
            var pos=new BlockPos((int)a[0],(int)a[1],(int)a[2]);
            api.WorldManager.LoadChunkColumnPriority(pos.X/32,pos.Z/32,new ChunkLoadOptions{KeepLoaded=true,OnLoaded=()=>
            {
                var block=api.World.BlockAccessor.GetBlock(pos);var next=api.World.BlockAccessor.GetBlock(pos.AddCopy(1,0,0));
                api.Logger.Notification("{0}","NSE_LOADED_MIGRATION_PROBE "+JsonSerializer.Serialize(new{firstId=block.Id,firstCode=block.Code?.ToString(),secondId=next.Id,secondCode=next.Code?.ToString()}));
            }});
            return TextCommandResult.Success("Existing fixture column requested through normal loader.");
        });
    }
}
