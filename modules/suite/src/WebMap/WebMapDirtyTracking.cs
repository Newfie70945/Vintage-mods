using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

public sealed partial class WebMapBridgeService
{
    private bool terrainDirtyEventsRegistered;
    private long lastTerrainDirtyWarningMs;
    private void RegisterTerrainDirtyTracking()
    {
        if(terrainDirtyEventsRegistered||dirtyJournal==null)return;
        api.Event.DidPlaceBlock+=OnMapBlockPlaced;
        api.Event.DidBreakBlock+=OnMapBlockBroken;
        terrainDirtyEventsRegistered=true;
    }
    private void StopTerrainDirtyTracking()
    {
        if(!terrainDirtyEventsRegistered)return;
        terrainDirtyEventsRegistered=false;
        try{api.Event.DidPlaceBlock-=OnMapBlockPlaced;}catch{}
        try{api.Event.DidBreakBlock-=OnMapBlockBroken;}catch{}
    }
    private void OnMapBlockPlaced(IServerPlayer player,int oldId,BlockSelection selection,ItemStack item)=>TrackTerrainChange(selection);
    private void OnMapBlockBroken(IServerPlayer player,int oldId,BlockSelection selection)=>TrackTerrainChange(selection);
    private void TrackTerrainChange(BlockSelection? selection)
    {
        if(disposed||!IsEnabled||selection?.Position?.dimension!=0)return;
        try{dirtyJournal?.MarkBlockDirty(0,selection.Position.X,selection.Position.Z);}
        catch(Exception ex)
        {
            long now=NowMs();if(now-lastTerrainDirtyWarningMs<30_000)return;lastTerrainDirtyWarningMs=now;
            try{api.Logger.Warning("[ServerEssentials/WebMap] dirty terrain tracking failed ({0}); inspect storage before updating the map.",ex.GetType().Name);}catch{}
        }
    }
}
