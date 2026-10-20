using System.Text.Json;
using Vintagestory.GameContent;
using System.Collections;
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
            var heldMethod=typeof(BlockIdRepairService).GetMethod("ReplaceLookedAtHeldOptions",BindingFlags.NonPublic|BindingFlags.Instance)!;
            bool unacknowledgedRejected=false;
            try{heldMethod.Invoke(repair,[player,"no-backup"]);}catch(TargetInvocationException e)when(e.InnerException?.Message.Contains("manual-copy-confirmed")==true){unacknowledgedRejected=true;}
            if(!unacknowledgedRejected)throw new Exception("Unacknowledged no-backup was accepted.");
            string message=(string)heldMethod.Invoke(repair,[player,"no-backup manual-copy-confirmed"])!;
            var config=JsonSerializer.SerializeToElement(repair.Snapshot()).GetProperty("loadedMigration").GetProperty("Plan");
            if(config.GetProperty("CreateBackup").GetBoolean()||!config.GetProperty("ManualBackupAcknowledged").GetBoolean())throw new Exception("Held no-backup policy was not saved.");
            int after=api.World.BlockAccessor.GetBlockId(pos);
            var ruleList=(TextCommandResult)typeof(BlockIdRepairService).GetMethod("ListMigrationRules",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[1])!;
            bool rulesShown=ruleList.StatusMessage.Contains("999997")&&ruleList.StatusMessage.Contains("game:rock-granite");if(!rulesShown)throw new Exception("Saved held rule was not visible in the in-game rule list.");
            if(before!=999997||after!=before||inventory[0].StackSize!=7)throw new Exception("Held rule changed terrain immediately or consumed held stack.");
            // The same adapter must reject a healthy looked-at source.
            data.EntityPlayer.BlockSelection=new(){Position=pos.AddCopy(-2,0,0)};bool healthyRejected=false;
            try{typeof(BlockIdRepairService).GetMethod("ReplaceLookedAtHeld",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[player]);}
            catch(TargetInvocationException e)when(e.InnerException?.Message.Contains("protected")==true){healthyRejected=true;}
            if(!healthyRejected)throw new Exception("Healthy looked-at source was not protected.");
            api.Logger.Notification("{0}","NSE_HELD_MIGRATION_PROBE "+JsonSerializer.Serialize(new{sourceId=before,heldQuantity=inventory[0].StackSize,healthyRejected,rulesShown,unacknowledgedRejected,noAutomaticBackup=true,message}));
            return TextCommandResult.Success("Held migration adapter checked using detached administrator fixture.");
        });
        api.ChatCommands.Create("nsechiselmigrationprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("X"),p.Int("Y"),p.Int("Z")).HandleWith(a=>
        {
            var repair=(BlockIdRepairService)typeof(BlockLoggerModSystem).GetField("blockRepair",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(api.ModLoader.GetModSystem<BlockLoggerModSystem>())!;
            var pos=new BlockPos((int)a[0],(int)a[1],(int)a[2]);int oldId=api.World.BlockAccessor.GetBlockId(pos);
            if(api.World.BlockAccessor.GetBlockEntity(pos)!=null)throw new Exception("Chisel probe would overwrite an existing entity.");
            var host=api.World.GetBlock(new AssetLocation("game:microblock"))??throw new Exception("Missing microblock asset.");var target=api.World.GetBlock(new AssetLocation("game:rock-granite"))??throw new Exception("Missing granite asset.");var healthy=api.World.GetBlock(new AssetLocation("game:rock-basalt"))??throw new Exception("Missing basalt asset.");
            api.World.BlockAccessor.SetBlock(host.Id,pos);var micro=(BlockEntityMicroBlock)api.World.BlockAccessor.GetBlockEntity(pos)!;
            micro.BlockIds=[999996,healthy.Id];micro.VoxelCuboids=[BlockEntityMicroBlock.ToUint(0,0,0,8,16,16,0),BlockEntityMicroBlock.ToUint(8,0,0,16,16,16,1)];var geometry=micro.VoxelCuboids.ToArray();
            var player=(ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));var data=(ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));const string uid="chisel-migration-test-admin";
            typeof(ServerWorldPlayerData).GetField("PlayerUID",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(data,uid);data.EntityPlayer=new EntityPlayer();typeof(ServerPlayer).GetField("worlddata",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,data);
            var inventory=new InventoryGeneric(11,"hotbar-"+uid,api);inventory[0].Itemstack=new ItemStack(target,7);var inventories=new Vintagestory.API.Datastructures.OrderedDictionary<string,InventoryBase>();inventories.Add("hotbar-"+uid,inventory);typeof(ServerPlayer).GetField("inventoryMgr",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,new ServerPlayerInventoryManager(inventories,player,(ServerMain)api.World));
            string at=$"at={pos.X},{pos.Y},{pos.Z} dimension=0";var inspect=(string)typeof(BlockIdRepairService).GetMethod("InspectMigrationTarget",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[player,at])!;
            if(!inspect.Contains("999996 (missing)"))throw new Exception("Inspector did not identify the embedded missing material.");
            bool ambiguousRejected=false;try{typeof(BlockIdRepairService).GetMethod("SelectMissingMaterial",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[new int[]{999996,999995},Array.Empty<string>()]);}catch(TargetInvocationException e)when(e.InnerException?.Message.Contains("Several")==true){ambiguousRejected=true;}
            if(!ambiguousRejected)throw new Exception("Ambiguous missing materials were guessed.");
            string message=(string)typeof(BlockIdRepairService).GetMethod("ReplaceLookedAtHeldOptions",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[player,"preserve source=999996 "+at+" no-backup manual-copy-confirmed"])!;
            int attempts=0;
            void Check(float dt)
            {
                if(!JsonSerializer.SerializeToElement(repair.Snapshot()).GetProperty("loadedMigration").GetProperty("active").GetBoolean())
                {if(++attempts>600)throw new Exception("Chisel activation timed out.");api.Event.RegisterCallback(Check,50);return;}
                if(micro.BlockIds[0]!=target.Id){if(++attempts>600)throw new Exception("Live chiseled material did not migrate.");api.Event.RegisterCallback(Check,50);return;}
                if(micro.BlockIds[1]!=healthy.Id||!micro.VoxelCuboids.SequenceEqual(geometry)||api.World.BlockAccessor.GetBlockId(pos)!=host.Id||inventory[0].StackSize!=7)throw new Exception("Chisel host/geometry/healthy material/held quantity changed.");
                // Serialize with the official engine, then exercise the actual
                // saved-load prefix on material-only damage (no missing host).
                micro.BlockIds[0]=999996;var chunk=(ServerChunk)api.World.BlockAccessor.GetChunkAtBlockPos(pos);byte[] before=chunk.ToBytes();object?[] arguments=[before,api.World,null];typeof(BlockIdRepairService).GetMethod("BeforeLoadedChunk",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,arguments);var after=(byte[])arguments[0]!;
                if(arguments[2]==null||before.SequenceEqual(after)||before.Length!=after.Length)throw new Exception("Saved hook ignored material-only damage.");
                var codec=typeof(BlockIdRepairService).Assembly.GetType("BlockLogger.WorldRepair.BlockIdChunkCodec")!;var fields=(IEnumerable)codec.GetMethod("Fields",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[before])!;int offset=-1;
                foreach(var field in fields){var type=field!.GetType();if((int)type.GetProperty("Tag")!.GetValue(field)! !=8)continue;int start=(int)type.GetProperty("Data")!.GetValue(field)!,length=(int)type.GetProperty("Length")!.GetValue(field)!;var summary=SavedBlockEntityInspector.Inspect(before.AsSpan(start,length).ToArray());if(summary.Class=="MicroBlock"&&summary.X==pos.X&&summary.Z==pos.Z)offset=start+(int)typeof(SavedBlockEntityInspector.EntitySummary).GetProperty("MaterialIdsOffset")!.GetValue(summary)!;}
                if(offset<0||System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(offset,4))!=target.Id||!Enumerable.Range(0,before.Length).Where(i=>i<offset||i>=offset+4).All(i=>before[i]==after[i]))throw new Exception("Saved hook changed geometry or healthy material bytes.");
                micro.BlockIds[0]=target.Id;api.World.BlockAccessor.SetBlock(oldId,pos);
                api.Logger.Notification("{0}","NSE_CHISEL_MIGRATION_PROBE "+JsonSerializer.Serialize(new{coordinateSelection=true,ambiguousRejected,liveMaterialMigrated=true,hostAndGeometryPreserved=true,healthyMaterialPreserved=true,savedMaterialOnlyHookPreservesOtherBytes=true,heldQuantity=inventory[0].StackSize,message}));
            }
            api.Event.RegisterCallback(Check,50);return TextCommandResult.Success("Testing chiseled material migration in disposable terrain.");
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
