using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.Server;
using Vintagestory.API.Common.Entities;
using System.Text.Json;
using BlockLogger;
using BlockLogger.WorldRepair;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

// Disposable server test only: exercises the actual selection adapter with a
// detached server-player fixture. No authenticated clients or game packets are simulated.
public sealed class SelectedRepairProbe : ModSystem
{
    public override void StartServerSide(ICoreServerAPI api)
    {
        var parsers=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseselectprobe").RequiresPrivilege(Privilege.controlserver).WithArgs(parsers.Int("X"),parsers.Int("Y"),parsers.Int("Z")).HandleWith(a=>{
            var repair=(BlockIdRepairService)typeof(BlockLoggerModSystem).GetField("blockRepair",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(api.ModLoader.GetModSystem<BlockLoggerModSystem>())!;
            var player=(ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
            var worlddata=(ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));
            typeof(ServerWorldPlayerData).GetField("PlayerUID",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(worlddata,"selection-test-administrator");
            var entity=new EntityPlayer();
            worlddata.EntityPlayer=entity;
            typeof(ServerPlayer).GetField("worlddata",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(player,worlddata);
            int x=(int)a[0],y=(int)a[1],z=(int)a[2];entity.BlockSelection=new(){Position=new BlockPos(x,y,z)};
            var select=typeof(BlockIdRepairService).GetMethod("SelectInGame",BindingFlags.NonPublic|BindingFlags.Instance)!;
            var focus=typeof(BlockIdRepairService).GetMethod("Focus",BindingFlags.NonPublic|BindingFlags.Instance)!;
            select.Invoke(repair,[player,""]);entity.BlockSelection=new(){Position=new BlockPos(x+1,y,z)};
            var locked=(BlockIdRepairService.SelectedFocus)focus.Invoke(repair,[player])!;
            if(locked.X!=x||locked.Y!=y||locked.Z!=z)throw new Exception("Selection moved with crosshair.");
            int attempts=0;
            void Finish(float dt){try{var inspection=(BlockIdRepairService.Inspection?)typeof(BlockIdRepairService).GetField("inspection",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(repair);if(inspection?.State=="running"&&++attempts<300){api.Event.RegisterCallback(Finish,100);return;}
                select.Invoke(repair,[player,"above"]);var above=(BlockIdRepairService.SelectedFocus)focus.Invoke(repair,[player])!;
                if(above.X!=x+1||above.Y!=y+1||above.Z!=z)throw new Exception("Above selection incorrect.");
                api.Logger.Notification("{0}","NSE_SELECTED_PROBE_RESULT "+JsonSerializer.Serialize(new{success=true,lockedSelection=locked,aboveSelection=above}));
            }catch(Exception e){api.Logger.Error("[SelectedRepairProbe] {0}",e.ToString());}}
            api.Event.RegisterCallback(Finish,100);return TextCommandResult.Success("Selection adapter probe queued.");
        });
    }
}
