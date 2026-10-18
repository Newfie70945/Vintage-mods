using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;

// Test server only. Loads exactly the selected existing fixture column so the
// normal engine, not the saved-byte inspector, validates the restored inventory.
public sealed class InventoryRestoreProbe : ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        var p=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseinvprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(p.Int("X"),p.Int("Y"),p.Int("Z")).HandleWith(a=>{
            var pos=new BlockPos((int)a[0],(int)a[1],(int)a[2]);bool already=api.World.BlockAccessor.GetBlockEntity(pos)!=null;int attempts=0;
            api.WorldManager.LoadChunkColumn(pos.X/32,pos.Z/32,true);
            void Check(float dt){var entity=api.World.BlockAccessor.GetBlockEntity(pos);
                if(entity is IBlockEntityContainer container){var inv=container.Inventory;var slots=new List<object>();for(int i=0;i<inv.Count;i++){var stack=inv[i]?.Itemstack;if(stack==null)continue;slots.Add(new{slot=i,code=stack.Collectible?.Code?.ToString(),quantity=stack.StackSize,text=stack.Attributes?.GetString("text","")??""});}
                    api.Logger.Notification("{0}","NSE_INV_PROBE_RESULT "+JsonSerializer.Serialize(new{success=true,block=api.World.BlockAccessor.GetBlock(pos).Code?.ToString(),slots=inv.Count,items=slots}));if(!already)api.WorldManager.UnloadChunkColumn(pos.X/32,pos.Z/32);}
                else if(++attempts<600)api.Event.RegisterCallback(Check,100);
                else {if(!already)api.WorldManager.UnloadChunkColumn(pos.X/32,pos.Z/32);api.Logger.Error("[InventoryRestoreProbe] No initialized inventory at fixture location after 600 polls.");}}
            api.Event.RegisterCallback(Check,100);return TextCommandResult.Success("Fixture column requested; awaiting normal block-entity initialization.");
        });
    }
}
