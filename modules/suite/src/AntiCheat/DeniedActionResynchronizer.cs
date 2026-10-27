using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace BlockLogger.AntiCheat;

internal sealed class DeniedActionResynchronizer
{
    private readonly ICoreServerAPI api;
    private readonly AntiCheatConfig config;
    private readonly Action<IServerPlayer,int,int,int,int,bool>? sendBlock;
    internal DeniedActionResynchronizer(ICoreServerAPI api,AntiCheatConfig config)
    {
        this.api=api;this.config=config;
        if(api.World is ServerMain server)
        {
            var method=typeof(ServerMain).GetMethod("SendSetBlock",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(IServerPlayer),typeof(int),typeof(int),typeof(int),typeof(int),typeof(bool)},null);
            if(method!=null)sendBlock=method.CreateDelegate<Action<IServerPlayer,int,int,int,int,bool>>(server);
        }
    }
    internal void Block(IServerPlayer player,BlockPos pos)
    {
        if(!config.PacketGuardDeniedActionResyncEnabled||sendBlock==null||player.Entity==null)return;
        var current=EntityRuntimeCompat.GetPos(player.Entity);
        if(current==null||current.Dimension!=pos.dimension)return;
        var accessor=api.World.BlockAccessor;
        if(accessor.GetChunkAtBlockPos(pos)==null)return;
        var actual=accessor.GetBlock(pos);
        if(actual==null||actual.IsMissing||actual.Code==null||actual.Id<0||actual.Id>=api.World.Blocks.Count)return;
        // Never use an unmasked fallback when OreShield was configured but its hook is unavailable.
        if(config.OreShieldEnabled){OreShieldService.TryResyncDeniedAction(player,pos);return;}
        sendBlock(player,actual.Id,pos.X,pos.InternalY,pos.Z,false);
    }
    internal void Slot(IServerPlayer player,string inventoryId,int slot)
    {
        if(!config.PacketGuardDeniedActionResyncEnabled||player.InventoryManager==null)return;
        if(!player.InventoryManager.GetInventory(inventoryId,out var inventory)||inventory==null||slot<0||slot>=inventory.Count)return;
        if(!inventory.HasOpened(player))return;
        if(inventory is InventoryBase native && native.InvNetworkUtil is InventoryNetworkUtil network)
        {
            var packet=network.getSlotUpdatePacket(player,slot);
            if(packet!=null)api.Network.SendArbitraryPacket(packet,player);
        }
    }
}
