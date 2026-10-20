using System.Text.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using BlockLogger;
using Vintagestory.Server;
using Vintagestory.API.Common.Entities;
using BlockLogger.WorldRepair;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.API.Datastructures;

// Test server only. Loads exactly the selected existing fixture column so the
// normal engine, not the saved-byte inspector, validates the restored inventory.
public sealed class InventoryRestoreProbe : ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        var p=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseinventoryhookprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Word("replay or changed")).HandleWith(a=>
        {
            string kind=(string)a[0];if(kind is not ("replay" or "changed"))return TextCommandResult.Error("Unsupported fixture.");
            var bytes=File.ReadAllBytes(Path.Combine(api.GetOrCreateDataPath("NSSInventoryProbe"),kind+".bin"));object?[] arguments=[bytes,api.World,null];
            typeof(BlockIdRepairService).GetMethod("BeforeLoadedChunk",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,arguments);
            bool unchanged=bytes.SequenceEqual((byte[])arguments[0]!);if(!unchanged||arguments[2]!=null)throw new Exception("Rejected inventory fixture changed or produced a successful-load marker.");
            api.Logger.Notification("{0}","NSE_INVENTORY_GUARD_PROBE "+JsonSerializer.Serialize(new{kind,unchanged,noLoadMarker=arguments[2]==null}));return TextCommandResult.Success("Inventory guard hook checked.");
        });
        api.ChatCommands.Create("nselivereconnectprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("X"),p.Int("Y"),p.Int("Z")).HandleWith(a=>
        {
            var repair=(BlockIdRepairService)typeof(BlockLoggerModSystem).GetField("blockRepair",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(api.ModLoader.GetModSystem<BlockLoggerModSystem>())!;
            var pos=new BlockPos((int)a[0],(int)a[1],(int)a[2]);var entity=api.World.BlockAccessor.GetBlockEntity(pos)??throw new Exception("Missing fixture entity.");var container=(IBlockEntityContainer)entity;var originalInventory=container.Inventory??throw new Exception("Missing inventory.");
            byte[] Attributes(ITreeAttribute attributes){using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))attributes.ToBytes(writer);return stream.ToArray();}
            var originalStack=originalInventory[0]!.Itemstack;var originalAttributes=originalStack?.Attributes is{} attrs?Attributes(attrs):null;int quantity=originalStack?.StackSize??0;
            var target=api.World.BlockAccessor.GetBlock(pos);var chunk=api.World.BlockAccessor.GetChunkAtBlockPos(pos);int index=((pos.Y&31)*32+(pos.Z&31))*32+(pos.X&31);chunk.Unpack();chunk.Data[index]=999998;chunk.MarkModified();
            var player=(ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));var data=(ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));const string uid="live-bookshelf-migration-test-admin";
            typeof(ServerWorldPlayerData).GetField("PlayerUID",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(data,uid);data.EntityPlayer=new EntityPlayer{BlockSelection=new(){Position=pos}};typeof(ServerPlayer).GetField("worlddata",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,data);
            var held=new InventoryGeneric(11,"hotbar-"+uid,api);held[0].Itemstack=new ItemStack(target,7);var inventories=new Vintagestory.API.Datastructures.OrderedDictionary<string,InventoryBase>();inventories.Add("hotbar-"+uid,held);typeof(ServerPlayer).GetField("inventoryMgr",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,new ServerPlayerInventoryManager(inventories,player,(ServerMain)api.World));
            string message=(string)typeof(BlockIdRepairService).GetMethod("ReplaceLookedAtHeldOptions",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(repair,[player,"reconnect confirm-inventory-risk no-backup manual-copy-confirmed"])!;
            int attempts=0;
            void Check(float dt)
            {
                if(api.World.BlockAccessor.GetBlockId(pos)!=target.Id){if(++attempts>600)throw new Exception("Live bookshelf reconnection timed out: "+JsonSerializer.Serialize(repair.Snapshot()));api.Event.RegisterCallback(Check,50);return;}
                if(!ReferenceEquals(entity,api.World.BlockAccessor.GetBlockEntity(pos))||!ReferenceEquals(container.Inventory,originalInventory)||!ReferenceEquals(originalInventory[0]!.Itemstack,originalStack)||(originalStack?.StackSize??0)!=quantity||held[0].StackSize!=7)throw new Exception("Live reconnection replaced inventory objects or items.");
                if(originalAttributes!=null&&!originalAttributes.SequenceEqual(Attributes(originalStack!.Attributes)))throw new Exception("Live reconnection changed item attributes.");
                api.Logger.Notification("{0}","NSE_LIVE_RECONNECT_PROBE "+JsonSerializer.Serialize(new{entityInstancePreserved=true,inventoryInstancePreserved=true,itemInstancesAndAttributesPreserved=true,quantity,emptyStayedEmpty=originalStack==null,heldQuantity=held[0].StackSize,message}));
            }
            api.Event.RegisterCallback(Check,50);return TextCommandResult.Success("Testing live retained container reconnection.");
        });
        api.ChatCommands.Create("nseinvprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("X"),p.Int("Y"),p.Int("Z")).HandleWith(a=>{
            var pos=new BlockPos((int)a[0],(int)a[1],(int)a[2]);bool already=api.World.BlockAccessor.GetBlockEntity(pos)!=null;int attempts=0;
            api.WorldManager.LoadChunkColumnPriority(pos.X/32,pos.Z/32,new ChunkLoadOptions{KeepLoaded=true});
            void Check(float dt){var entity=api.World.BlockAccessor.GetBlockEntity(pos);
                if(entity is IBlockEntityContainer container){var inv=container.Inventory;var slots=new List<object>();for(int i=0;i<inv.Count;i++){var stack=inv[i]?.Itemstack;if(stack==null)continue;slots.Add(new{slot=i,code=stack.Collectible?.Code?.ToString(),quantity=stack.StackSize,text=stack.Attributes?.GetString("text","")??""});}
                    api.Logger.Notification("{0}","NSE_INV_PROBE_RESULT "+JsonSerializer.Serialize(new{success=true,block=api.World.BlockAccessor.GetBlock(pos).Code?.ToString(),slots=inv.Count,items=slots}));}
                else if(++attempts<600)api.Event.RegisterCallback(Check,100);
                else {api.Logger.Error("[InventoryRestoreProbe] No initialized inventory at fixture location after 600 polls.");}}
            api.Event.RegisterCallback(Check,100);return TextCommandResult.Success("Fixture column requested; awaiting normal block-entity initialization.");
        });
    }
}
