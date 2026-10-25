using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

internal sealed partial class WebMapTerrainExporter
{
    private bool loadedDirtyOnly;
    private readonly HashSet<ChunkCoord> capturedResidentChunks=new();
    private long residentReadTicks;

    // Independent automatic pass: reads resident terrain only, never the world loader or save DB.
    public async Task<bool> ExportLoadedDirtyRuntimeAsync(WebMapConfig config,CancellationToken cancellationToken)
    {
        automaticSavedTerrainOnly=true;
        loadedDirtyOnly=true;
        capturedResidentChunks.Clear();
        residentReadTicks=0;
        var watch=Stopwatch.StartNew();
        var status=new WebMapTerrainExportStatus {State="exporting",Phase="loaded-dirty",Trigger="automatic-loaded-dirty",
            ExportMode="loaded-dirty-incremental",TerrainReadMode="resident-only",TileSize=config.TerrainTileSize,
            StartedUnixMilliseconds=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Message="Checking dirty terrain that is already loaded…"};
        string current=Path.Combine(rootPath,"terrain"),stage=Path.Combine(rootPath,"terrain-next");
        try
        {
            ResetSurfaceDiagnostics();
            var manifest=LoadManifest(current);
            string saveId=api.WorldManager.SaveGame.SavegameIdentifier??"";
            if(manifest==null||manifest.Version!=ManifestVersion||manifest.TileSize!=config.TerrainTileSize||manifest.SavegameIdentifier!=saveId)
                throw new InvalidOperationException("A compatible initial map export is required before loaded-terrain updates.");
            status.TotalChunkColumns=manifest.ChunkColumnCount;
            long mutationVersion=0;
            var snapshot=dirtyJournal?.Snapshot(out mutationVersion)??new HashSet<WebMapDirtyChunkCoord>();
            var batch=dirtyJournal?.NextLiveCandidates(snapshot,2048)??Array.Empty<WebMapDirtyChunkCoord>();
            // Only patch existing raw tiles. Newly explored tiles retain the existing saved-exploration path.
            var candidates=batch.Where(d=>manifest.Tiles.ContainsKey(TileKey(FloorDiv(d.ChunkX*ChunkSize,config.TerrainTileSize),FloorDiv(d.ChunkZ*ChunkSize,config.TerrainTileSize))) &&
                File.Exists(RawTilePath(current,FloorDiv(d.ChunkX*ChunkSize,config.TerrainTileSize),FloorDiv(d.ChunkZ*ChunkSize,config.TerrainTileSize)))).ToArray();
            var eligible=await FilterResidentCandidatesAsync(candidates,cancellationToken).ConfigureAwait(false);
            snapshot.IntersectWith(eligible);
            status.DirtyChunkCount=snapshot.Count;
            status.DeferredDirtyChunkCount=batch.Length-snapshot.Count;
            publishStatus(status.Clone());
            if(snapshot.Count>0)
            {
                var tiles=manifest.Tiles.Values.Select(t=>new TileCoord(t.X,t.Z)).ToHashSet();
                var chunks=snapshot.Select(d=>new ChunkCoord(d.ChunkX,d.ChunkZ)).ToHashSet();
                var coverage=new TerrainCoverage(tiles,chunks,manifest.ChunkColumnCount);
                await IncrementalExportRuntimeAsync(config,coverage,stage,current,manifest,snapshot,status,null,chunks,cancellationToken).ConfigureAwait(false);
                // A column can unload between selection and capture. Only published captures may be acknowledged.
                snapshot.RemoveWhere(d=>!capturedResidentChunks.Contains(new ChunkCoord(d.ChunkX,d.ChunkZ)));
                dirtyJournal?.TryAcknowledgeSnapshot(snapshot,mutationVersion);
            }
            status.ReusedTiles=Math.Max(0,manifest.Tiles.Count-status.WrittenTiles);
            status.DeferredDirtyChunkCount=Math.Max(0,batch.Length-capturedResidentChunks.Count);
            status.State="ready";status.Phase="complete";status.ProgressPercent=100;
            status.ProcessedChunkColumns=capturedResidentChunks.Count;
            status.Message=$"Loaded-terrain update complete: {capturedResidentChunks.Count:n0} dirty columns updated, {status.DeferredDirtyChunkCount:n0} candidates deferred. Unloaded edits remain queued.";
            return true;
        }
        catch(OperationCanceledException){status.State="ready";status.Phase="cancelled";status.Message="Loaded-terrain update cancelled; unfinished dirty work remains queued.";return false;}
        catch(Exception ex)
        {
            status.State="failed";status.Phase="failed";status.LastError=ex.GetType().Name+": "+ex.Message;
            status.Message="Loaded-terrain update deferred; dirty work remains queued. No chunks were requested.";
            api.Logger.Warning("[ServerEssentials/WebMap] loaded-only terrain update deferred ({0}).",ex.GetType().Name);
            return false;
        }
        finally
        {
            try{if(Directory.Exists(stage))Directory.Delete(stage,true);}catch{}
            watch.Stop();status.TotalElapsedMilliseconds=watch.ElapsedMilliseconds;status.CompletedUnixMilliseconds=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if(status.State=="ready"&&status.Phase=="complete")status.LastSuccessfulUnixMilliseconds=status.CompletedUnixMilliseconds;
            status.WorldReadMilliseconds=(long)(Interlocked.Read(ref residentReadTicks)*1000d/Stopwatch.Frequency);
            status.WorldLoaderChunkColumns=0;status.ExportResourcesReleased=true;
            blockColorCache.Clear();blockColorById=Array.Empty<Rgb>();Volatile.Write(ref blockPalettePrimed,0);
            capturedResidentChunks.Clear();
            publishStatus(status.Clone());RecordTimingSummary(status);
        }
    }

    private async Task<HashSet<WebMapDirtyChunkCoord>> FilterResidentCandidatesAsync(WebMapDirtyChunkCoord[] candidates,CancellationToken token)
    {
        var result=new HashSet<WebMapDirtyChunkCoord>();int index=0;
        while(index<candidates.Length)
        {
            token.ThrowIfCancellationRequested();
            var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration=token.Register(()=>done.TrySetCanceled(token));
            api.Event.EnqueueMainThreadTask(()=>
            {
                if(done.Task.IsCompleted)return;
                try
                {
                    long start=Stopwatch.GetTimestamp();int checks=0;
                    do
                    {
                        var d=candidates[index++];
                        try{if(api.WorldManager.GetChunk(d.ChunkX,0,d.ChunkZ)!=null)result.Add(d);}catch{ /* Defer this candidate; other columns still get a turn. */ }
                        checks++;
                    }while(index<candidates.Length&&checks<64&&Stopwatch.GetElapsedTime(start).TotalMilliseconds<2);
                    done.TrySetResult(true);
                }
                catch(Exception ex){done.TrySetException(ex);}
            },"nses-webmap-loaded-eligibility");
            await done.Task.ConfigureAwait(false);
            if(index<candidates.Length)await Task.Delay(10,token).ConfigureAwait(false);
        }
        return result;
    }

    private sealed class ResidentSurface
    {
        public int Pixel,Y;
        public readonly int[] Ids=new int[ChunkSize*ChunkSize];
        public readonly ushort[] Heights=new ushort[ChunkSize*ChunkSize];
    }
    private async Task<bool> TryRenderResidentColumnAsync(int chunkX,int chunkZ,byte[] rgba,int tileSize,int offsetX,int offsetZ,CancellationToken token)
    {
        int height=api.WorldManager.MapSizeY;
        if(height<1||height>ushort.MaxValue)throw new InvalidOperationException("Unsupported world height for loaded terrain capture.");
        var surface=new ResidentSurface {Y=height-1};
        while(surface.Pixel<ChunkSize*ChunkSize)
        {
            token.ThrowIfCancellationRequested();
            var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration=token.Register(()=>done.TrySetCanceled(token));
            api.Event.EnqueueMainThreadTask(()=>
            {
                if(done.Task.IsCompleted)return;
                try{done.TrySetResult(CaptureResidentSurfaceStep(chunkX,chunkZ,height,surface));}
                catch(Exception ex){done.TrySetException(ex);}
            },"nses-webmap-read-resident-surface");
            if(!await done.Task.ConfigureAwait(false))return false;
            if(surface.Pixel<ChunkSize*ChunkSize)await Task.Delay(25,token).ConfigureAwait(false);
        }
        // No chunk/entity/map-chunk references escape the main-thread step. Paint only completed captures.
        ClearChunkPatch(rgba,tileSize,offsetX,offsetZ);
        int resolved=0,mapHeight=Math.Max(1,height-1);
        for(int pixel=0;pixel<surface.Ids.Length;pixel++)
        {
            int id=surface.Ids[pixel];if(id==0)continue;resolved++;
            var color=GetBlockColor(id);int x=offsetX+pixel%ChunkSize,z=offsetZ+pixel/ChunkSize;
            int offset=(z*tileSize+x)*4;
            rgba[offset]=color.R;rgba[offset+1]=color.G;rgba[offset+2]=color.B;
            rgba[offset+3]=(byte)Math.Clamp((int)Math.Round(surface.Heights[pixel]*255.0/mapHeight),1,255);
        }
        Interlocked.Add(ref surfaceSampleCount,ChunkSize*ChunkSize);Interlocked.Add(ref resolvedSurfaceSampleCount,resolved);
        Interlocked.Add(ref unresolvedSurfaceSampleCount,ChunkSize*ChunkSize-resolved);
        return true;
    }
    private bool CaptureResidentSurfaceStep(int chunkX,int chunkZ,int height,ResidentSurface surface)
    {
        long workStarted=Stopwatch.GetTimestamp();
        int vertical=(height+ChunkSize-1)/ChunkSize;
        var chunks=ArrayPool<IServerChunk>.Shared.Rent(vertical);
        var unpacked=ArrayPool<bool>.Shared.Rent(vertical);
        Array.Clear(chunks,0,chunks.Length);Array.Clear(unpacked,0,unpacked.Length);
        try
        {
            for(int y=0;y<vertical;y++)
            {
                chunks[y]=api.WorldManager.GetChunk(chunkX,y,chunkZ);
                if(chunks[y]==null)return false; // Never request a missing section; retry after a natural load.
            }
            long start=Stopwatch.GetTimestamp();int reads=0;
            do
            {
                if(surface.Y<0){surface.Pixel++;surface.Y=height-1;continue;}
                int cy=surface.Y/ChunkSize;
                var chunk=chunks[cy];
                if(chunk.Empty){surface.Y=cy*ChunkSize-1;reads++;continue;}
                EnsureChunkUnpacked(chunk,unpacked,cy);
                var data=chunk.Data;if(data==null)return false;
                int x=surface.Pixel%ChunkSize,z=surface.Pixel/ChunkSize;
                int id=data.GetBlockId(((surface.Y%ChunkSize)*ChunkSize+z)*ChunkSize+x,BlockLayersAccess.Default);reads++;
                if(id==0)surface.Y--;
                else{surface.Ids[surface.Pixel]=id;surface.Heights[surface.Pixel]=(ushort)surface.Y;surface.Pixel++;surface.Y=height-1;}
            }while(surface.Pixel<ChunkSize*ChunkSize && reads<2048 && Stopwatch.GetElapsedTime(start).TotalMilliseconds<2);
            return true;
        }
        finally
        {
            ArrayPool<IServerChunk>.Shared.Return(chunks,true);ArrayPool<bool>.Shared.Return(unpacked);
            long elapsed=Stopwatch.GetTimestamp()-workStarted;Interlocked.Add(ref residentReadTicks,elapsed);
            SuiteTimingRegistry.Record("Web Map resident surface read",elapsed*1000d/Stopwatch.Frequency);
        }
    }
}
