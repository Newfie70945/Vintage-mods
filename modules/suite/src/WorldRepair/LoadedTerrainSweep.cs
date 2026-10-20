using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Server;
using Vintagestory.GameContent;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    // One snapshot per activation, keys only: do not retain chunks or load terrain.
    private Queue<long> liveChunkKeys=new();
    private long? liveChunkKey;
    private int liveCell;
    private Queue<BlockPos> liveMicroPositions=new();
    private bool liveMicroPrepared;
    private long liveSweepTick,liveSweepBlocks,liveSweepSkipped;
    private string liveSweepIssue="";
    private readonly MigrationSkipHistory migrationSkips=new();
    private void RecordMigrationSkip(BlockPos pos,int source,int target,string reason)=>migrationSkips.Record(pos.X,pos.Y,pos.Z,pos.dimension,source,target,reason);
    private void StartLoadedTerrainSweep()
    {
        StopLoadedTerrainSweep();
        liveChunkKeys=new(api.WorldManager.AllLoadedChunks.Keys);
        liveSweepTick=api.Event.RegisterGameTickListener(_=>SweepLoadedTerrain(),50);
    }
    private void StopLoadedTerrainSweep()
    {
        if(liveSweepTick!=0)api.Event.UnregisterGameTickListener(liveSweepTick);
        liveSweepTick=0;liveChunkKeys.Clear();liveChunkKey=null;liveCell=0;liveMicroPositions.Clear();liveMicroPrepared=false;
    }
    private void SweepLoadedTerrain()
    {
        lock(loadedGate)
        {
            if(disposed||!loadedActive){StopLoadedTerrainSweep();return;}
            var watch=Stopwatch.StartNew();int reads=0,writes=0,chunks=0;
            try
            {
                var server=(ServerMain)api.World;
                long? cachedKey=null;IServerChunk? chunk=null;BlockPos? origin=null;
                while(reads<4096&&writes<32&&chunks<16&&watch.Elapsed.TotalMilliseconds<2)
                {
                    if(!liveChunkKey.HasValue)
                    {
                        if(!liveChunkKeys.TryDequeue(out long key)){StopLoadedTerrainSweep();return;}
                        liveChunkKey=key;liveCell=0;liveMicroPrepared=false;liveMicroPositions.Clear();chunks++;
                    }
                    if(cachedKey!=liveChunkKey)
                    {
                        var coord=server.WorldMap.ChunkPosFromChunkIndex3D(liveChunkKey.Value);
                        origin=new BlockPos(coord.X*32,coord.Y*32,coord.Z*32,coord.Dimension);
                        chunk=api.WorldManager.GetChunk(origin);cachedKey=liveChunkKey;
                    }
                    if(chunk==null||origin==null){liveChunkKey=null;continue;}
                    if(!liveMicroPrepared)
                    {
                        liveMicroPositions=new(chunk.BlockEntities.Values.OfType<BlockEntityMicroBlock>().Select(e=>e.Pos.Copy()));liveMicroPrepared=true;
                    }
                    if(liveMicroPositions.TryDequeue(out var microPos))
                    {
                        reads++;
                        if(api.World.BlockAccessor.GetBlockEntity(microPos) is BlockEntityMicroBlock micro)
                        {long count=MigrateLiveChiseled(micro);if(count>0){migratedMaterialReferences+=count;chunk.MarkModified();writes++;}}
                        continue;
                    }
                    if(liveCell==0)
                    {
                        chunk.Unpack_ReadOnly();
                        if(!loadedReplacements.Keys.Any(chunk.Data.ContainsBlock)){liveChunkKey=null;continue;}
                    }
                    int index=liveCell++;reads++;
                    int source=chunk.UnpackAndReadBlock(index,BlockLayersAccess.Solid);
                    if(loadedReplacements.TryGetValue(source,out int target)&&Missing(source))
                    {
                        var pos=origin.AddCopy(index&31,index>>10,(index>>5)&31);
                        var destination=api.World.Blocks[target];
                        var retained=api.World.BlockAccessor.GetBlockEntity(pos);
                        bool compatible=retained!=null&&loadedIdentities.TryGetValue(source,out var identity)&&
                            identity.EntityClass.Length>0&&api.World.ClassRegistry.GetBlockEntityClass(retained.GetType())==identity.EntityClass;
                        // Historical recovery needs the saved-byte processor and its
                        // receipts. Never replace contents or invent a live entity.
                        if(loadedReconnectSources.Contains(source))
                        {
                            try
                            {
                                if(retained!=null&&TryReconnectLive(pos,source,target,retained)){liveSweepBlocks++;loadedInventoriesPreserved++;writes++;chunk.MarkModified();}
                                else {liveSweepSkipped++;liveSweepIssue="Reconnect requires compatible surviving container metadata; reload for saved metadata, or use logged recovery if contents were lost.";RecordMigrationSkip(pos,source,target,liveSweepIssue);}
                            }
                            catch(Exception error){liveSweepSkipped++;loadedInventoryLastIssue=error.Message;RecordMigrationSkip(pos,source,target,error.Message);}
                            if(liveCell==32768)liveChunkKey=null;
                            continue;
                        }
                        bool unsafeCell=loadedInventorySources.Contains(source)||destination.ForFluidsLayer||
                            chunk.UnpackAndReadBlock(index,BlockLayersAccess.Fluid)!=0||
                            (retained!=null?!compatible:!string.IsNullOrEmpty(destination.EntityClass));
                        if(unsafeCell)
                        {
                            liveSweepSkipped++;liveSweepIssue=loadedInventorySources.Contains(source)?"Historical recovery requires saved-chunk loading; loaded contents were left unchanged.":destination.ForFluidsLayer||chunk.UnpackAndReadBlock(index,BlockLayersAccess.Fluid)!=0?"Fluid layer conflict; choose a compatible solid replacement.":retained!=null?"Retained block entity is incompatible with this rule; choose its original identity or review reconnection.":"Replacement needs block-entity metadata that is not live; migration will not invent it.";
                            RecordMigrationSkip(pos,source,target,liveSweepIssue);
                        }
                        else if(api.World.BlockAccessor.GetBlockId(pos)==source)
                        {
                            api.World.BlockAccessor.ExchangeBlock(target,pos);
                            api.World.BlockAccessor.MarkBlockDirty(pos);
                            chunk.MarkModified();liveSweepBlocks++;writes++;
                        }
                    }
                    if(liveCell==32768)liveChunkKey=null;
                }
            }
            catch(Exception error)
            {
                liveSweepIssue="Loaded-terrain pass stopped: "+error.Message+" Saved-chunk migration remains active.";
                api.Logger.Warning("[BlockMigration] {0}",liveSweepIssue);StopLoadedTerrainSweep();
            }
        }
    }
}
