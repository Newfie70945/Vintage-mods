using System.Globalization;
using System.Security.Cryptography;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed record SelectedRequest(int X,int Y,int Z,int Dimension,string Destination,bool AllowAirSource=false,bool RiskAcknowledged=false,bool CreateBackup=true,bool ManualBackupAcknowledged=false);
    public sealed class SelectedPlan
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N");
        public string State {get;set;}="running";
        public string World {get;set;}="";
        public string Registry {get;set;}="";
        public SelectedRequest Request {get;set;}=new(0,0,0,0,"");
        public int Source {get;set;}
        public int Target {get;set;}
        public string SourceCode {get;set;}="";
        public string TargetCode {get;set;}="";
        public string ChunkKey {get;set;}="";
        public string ChunkHash {get;set;}="";
        public DateTime CreatedUtc {get;set;}=DateTime.UtcNow;
        public string Message {get;set;}="";
        public string Backup {get;set;}="";
        public bool Changed {get;set;}
    }
    public sealed record SelectedFocus(string World,int X,int Y,int Z,int Dimension,DateTime SelectedUtc);
    private readonly Dictionary<string,SelectedFocus> selectedByAdmin=new(StringComparer.Ordinal);
    private SelectedFocus? selectedFocus;
    private SelectedPlan? selectedPreview,selectedPending,selectedLast;
    private string SelectedPendingFile=>Path.Combine(directory,"selected-pending.json");
    private string SelectedLastFile=>Path.Combine(directory,"selected-last.json");
    private void LoadSelectedRepair()
    {try{selectedPending=Read<SelectedPlan>(SelectedPendingFile);selectedLast=Read<SelectedPlan>(SelectedLastFile);}catch(Exception e){selectedLast=new(){State="failed",Message="Selected repair state is unreadable: "+e.Message};}}
    private static void ValidateSelectedRequest(SelectedRequest r)
    {
        if(r.X<0||r.Z<0||r.X/32>4194303||r.Z/32>4194303||r.Y<0||r.Y/32>511||r.Dimension<0||r.Dimension>1023)throw new ArgumentException("Use absolute saved coordinates and a valid dimension.");
        if(!r.RiskAcknowledged)throw new InvalidOperationException("Review the target and acknowledge this experimental one-position repair with confirm-risk.");
        if(!r.CreateBackup&&!r.ManualBackupAcknowledged)throw new InvalidOperationException("Make and acknowledge a manual world copy before disabling automatic backup.");
    }
    public object PreviewSelected(SelectedRequest request)
    {
        Guard();ValidateSelectedRequest(request);lock(gate)
        {
            if(worker is{IsCompleted:false}||pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Wait for the active job or cancel the staged repair first.");
            int target=ResolveTarget(request.Destination);if(target<=0||Missing(target))throw new ArgumentException("Selected repair requires a registered non-air destination.");
            var block=api.World.Blocks[target];if(block.ForFluidsLayer||!string.IsNullOrEmpty(block.EntityClass))throw new InvalidOperationException("This destination needs fluid/container metadata. For a retained bookshelf/container, use retained inventory restoration; do not manufacture its metadata.");
            var map=Mapping();var codes=api.World.Blocks.Select(b=>b?.Code?.ToString()??"").ToArray();var p=new SelectedPlan{World=api.World.SavegameIdentifier,Registry=Fingerprint(map),Request=request,Target=target,TargetCode=block.Code.ToString(),ChunkKey=unchecked((long)ChunkPos.ToChunkIndex(request.X/32,request.Y/32,request.Z/32,request.Dimension)).ToString(CultureInfo.InvariantCulture)};
            string path=Database().DatabaseFilename;var server=(ServerMain)api.World;selectedPreview=new(){Id=p.Id,Request=request};cancel?.Dispose();cancel=new();var token=cancel.Token;
            worker=Task.Run(()=>{var pool=new ChunkDataPool(32,server){CacheSize=4};try{
                token.ThrowIfCancellationRequested();var bytes=ReadRestoreChunk(path,p.ChunkKey);var codec=new BlockIdChunkCodec(pool);int index=((request.Y&31)*32+(request.Z&31))*32+(request.X&31);
                p.Source=codec.ReadCells(bytes,[index])[index][0];p.SourceCode=map.GetValueOrDefault(p.Source,p.Source>=0&&p.Source<codes.Length?codes[p.Source]:"");
                if(p.Source==0&&!request.AllowAirSource)throw new InvalidOperationException("This position is already saved as air. A missing plant cannot be identified from air alone. Review its history, then explicitly allow-air-source for this one location if appropriate.");
                codec.ReplaceAt(bytes,request.X,request.Y,request.Z,request.Dimension,p.Source,target);p.ChunkHash=Convert.ToHexString(SHA256.HashData(bytes));token.ThrowIfCancellationRequested();p.State="preview";p.Message=$"Exactly one solid block: {p.SourceCode} (ID {p.Source}) → {p.TargetCode} (ID {p.Target}) at {request.X},{request.Y},{request.Z}, dimension {request.Dimension}. Other occurrences of the same ID are excluded. Stage for restart after reviewing your backup.";
            }catch(OperationCanceledException){p.State="cancelled";p.Message="Selected preview cancelled.";}catch(Exception e){p.State="failed";p.Message=e.Message;}finally{pool.FreeAll();lock(gate){if(selectedPreview?.Id==p.Id)selectedPreview=p;}}});return new{success=true,selectedId=p.Id};
        }
    }
    public SelectedPlan StageSelected(string id,SelectedRequest? expected=null)
    {
        Guard();lock(gate){if(LoadedConfigured())throw new InvalidOperationException("Disable loaded-chunk migration before staging another repair.");var p=selectedPreview;if(p==null||p.Id!=id||p.State!="preview"||DateTime.UtcNow-p.CreatedUtc>TimeSpan.FromMinutes(15))throw new InvalidOperationException("A fresh successful selected-location preview is required.");
            if(expected!=null&&expected!=p.Request)throw new InvalidOperationException("Selected repair inputs changed; preview again.");
            if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Another repair is staged.");
            ValidateSelectedRequest(p.Request);if(p.World!=api.World.SavegameIdentifier||p.Registry!=Fingerprint(Mapping()))throw new InvalidOperationException("World or registry changed.");
            p.State="pending";Write(SelectedPendingFile,p);selectedPending=p;selectedPreview=null;return p;}
    }
    private void ApplySelectedBeforeTerrainLoads()
    {
        var p=selectedPending!;bool writes=false;ChunkDataPool? pool=null;
        try{
            Guard();ValidateSelectedRequest(p.Request);if(pending!=null||restorePending!=null)throw new InvalidOperationException("Conflicting repair jobs; cancel and preview again.");
            var r=p.Request;if(p.World!=api.World.SavegameIdentifier||p.Registry!=Fingerprint(Mapping())||ResolveTarget(r.Destination)!=p.Target||Missing(p.Target))throw new InvalidOperationException("World, registry or selected destination changed.");
            var target=api.World.Blocks[p.Target];if(target.Code.ToString()!=p.TargetCode||target.ForFluidsLayer||!string.IsNullOrEmpty(target.EntityClass))throw new InvalidOperationException("Destination class/code changed.");
            var server=(ServerMain)api.World;var thread=(ChunkServerThread)AccessTools.Field(typeof(ServerMain),"chunkThread").GetValue(server)!;
            void Quiescent(){var queue=AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns").GetValue(thread)!;if(api.Server.CurrentRunPhase!=EnumServerRunPhase.AssetsFinalize||thread.runOffThreadSaveNow||api.WorldManager.AllLoadedChunks.Count>0||Convert.ToInt32(AccessTools.Property(queue.GetType(),"Count").GetValue(queue))>0)throw new InvalidOperationException("Selected repair requires startup before terrain loads or saves.");}
            Quiescent();var db=Database();pool=new ChunkDataPool(32,server){CacheSize=4};var codec=new BlockIdChunkCodec(pool);
            byte[] Prepare(){var bytes=ReadRestoreChunk(db.DatabaseFilename,p.ChunkKey);if(Convert.ToHexString(SHA256.HashData(bytes))!=p.ChunkHash)throw new InvalidOperationException("Selected saved chunk changed after preview. No replacement applied; save and preview again.");if(p.Source==0&&!r.AllowAirSource)throw new InvalidOperationException("Air source was not acknowledged.");return codec.ReplaceAt(bytes,r.X,r.Y,r.Z,r.Dimension,p.Source,p.Target);}
            var output=Prepare();if(r.CreateBackup){p.Backup=Path.Combine(directory,"selected-world-before-"+p.Id+".vcdbs");db.CreateBackup(p.Backup);using var backup=Open(p.Backup);using var check=backup.CreateCommand();check.CommandTimeout=600;check.CommandText="PRAGMA quick_check";if(Convert.ToString(check.ExecuteScalar())!="ok")throw new InvalidDataException("World backup failed integrity verification.");}
            output=Prepare();Quiescent();p.State="applying";Write(SelectedLastFile,p);File.Move(SelectedPendingFile,Path.Combine(directory,"selected-consumed-"+p.Id+".json"),true);selectedPending=null;
            writes=true;db.SetChunks([new DbChunk(ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)long.Parse(p.ChunkKey,CultureInfo.InvariantCulture))),output)]);
            p.State="complete";p.Changed=true;p.Message="Selected block repaired; exactly one solid position changed. Refresh lighting/map caches if needed.";
        }catch(Exception e){p.State=writes?"failed-uncertain":"failed";p.Message=e.Message+(writes?" Inspect and restore your backup before continuing.":" No edits were made.");api.Logger.Error("[SelectedBlockRepair] {0}",p.Message);if(File.Exists(SelectedPendingFile))File.Move(SelectedPendingFile,Path.Combine(directory,"selected-rejected-"+p.Id+".json"),true);selectedPending=null;if(writes)RequestRepairFailureShutdown("Selected block repair failed; inspect backup");}
        finally{pool?.FreeAll();selectedLast=p;Write(SelectedLastFile,p);}
    }
    private SelectedFocus Focus(IServerPlayer player)
    {
        foreach(var uid in selectedByAdmin.Where(x=>DateTime.UtcNow-x.Value.SelectedUtc>TimeSpan.FromMinutes(15)).Select(x=>x.Key).ToArray())selectedByAdmin.Remove(uid);
        if(!selectedByAdmin.TryGetValue(player.PlayerUID,out var f)||f.World!=api.World.SavegameIdentifier)throw new InvalidOperationException("Use /nseblockrepair select first. Selections expire after 15 minutes.");return f;
    }
    private string SelectInGame(IServerPlayer player,string arguments)
    {
        var args=arguments.Split(' ',StringSplitOptions.RemoveEmptyEntries);BlockPos? pos=player.CurrentBlockSelection?.Position?.Copy();
        if(args.Length==1&&args[0]=="above"){if(pos==null)throw new ArgumentException("Look at the ground under the missing plant first.");pos.Y++;}
        else if(args.Length>0){if(args[0]=="at")args=args.Skip(1).ToArray();if(args.Length is not (3 or 4)||!int.TryParse(args[0],out var x)||!int.TryParse(args[1],out var y)||!int.TryParse(args[2],out var z))throw new ArgumentException("Use select, select above, or select at X Y Z [dimension].");int dim=0;if(!BlockLogger.Services.EntityPositionResolver.TryGetBlockPos(player.Entity,out var own))throw new ArgumentException("Player position unavailable.");dim=own.dimension;if(args.Length==4&&(!int.TryParse(args[3],out dim)||dim<0))throw new ArgumentException("Invalid dimension.");pos=new BlockPos(x,y,z,dim);}
        if(pos==null)throw new ArgumentException("Look at a block, or use select above / select at X Y Z.");StartInspection(pos.X,pos.Y,pos.Z,pos.dimension);
        if(selectedByAdmin.Count>=128)selectedByAdmin.Remove(selectedByAdmin.OrderBy(x=>x.Value.SelectedUtc).First().Key);
        var f=new SelectedFocus(api.World.SavegameIdentifier,pos.X,pos.Y,pos.Z,pos.dimension,DateTime.UtcNow);selectedByAdmin[player.PlayerUID]=f;selectedFocus=f;
        return $"Selected absolute {f.X},{f.Y},{f.Z}, dimension {f.Dimension}. Reading saved data; /nseblockrepair results shows ID, retained containers and recovery actions. /nseblockrepair suggest looks up logged replacements.";
    }
    private string SelectedResults(IServerPlayer player)
    {
        var f=Focus(player);var lines=new List<string>{$"Selected {f.X},{f.Y},{f.Z}, dimension {f.Dimension}."};
        if(inspection?.World==f.World&&inspection.X==f.X&&inspection.Y==f.Y&&inspection.Z==f.Z&&inspection.Dimension==f.Dimension){lines.Add($"Inspection {inspection.State}: saved solid ID {inspection.SolidId}, {inspection.SolidCode}. {inspection.Message}");foreach(var e in inspection.Entities.Take(3))lines.Add($"Retained {e.Class}: {e.SavedBlockCode}, {e.NonEmptyStacks} stacks. {e.Assessment}");}
        if(recommendations?.World==f.World&&recommendations.X==f.X&&recommendations.Y==f.Y&&recommendations.Z==f.Z&&recommendations.Dimension==f.Dimension){lines.Add("Suggestions: "+recommendations.State+". "+recommendations.Message);foreach(var c in recommendations.Candidates.Take(5))lines.Add($"{c.Code}, ID {c.CurrentId}: {c.Confidence}, {c.SupportingRecords} records. {c.Reason}");}
        if(selectedPreview!=null){lines.Add($"Selected preview {selectedPreview.Id}: {selectedPreview.State}. {selectedPreview.Message}");if(selectedPreview.State=="preview")lines.Add($"Stage this exact location: /nseblockrepair selectedstage {selectedPreview.Id}");}
        if(restorePreview!=null){lines.Add($"Inventory preview {restorePreview.Id}: {restorePreview.State}. {restorePreview.Message}");if(restorePreview.State=="preview")lines.Add($"Stage retained recovery: /nseblockrepair restorestage {restorePreview.Id}");}
        return string.Join("\n",lines);
    }
    private string ReplaceSelected(IServerPlayer player,string destination,string options)
    {
        var f=Focus(player);var flags=options.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if(flags.Any(x=>x is not ("confirm-risk" or "allow-air-source" or "no-backup" or "manual-copy-confirmed")))throw new ArgumentException("Options: confirm-risk, allow-air-source, no-backup manual-copy-confirmed.");
        int target=ResolveTarget(destination);
        if(!Missing(target)&&!string.IsNullOrEmpty(api.World.Blocks[target].EntityClass)){
            if(inspection?.State!="complete"||inspection.World!=f.World||inspection.X!=f.X||inspection.Y!=f.Y||inspection.Z!=f.Z||inspection.Dimension!=f.Dimension||!inspection.Entities.Any(e=>e.Complete&&Canonical(e.SavedBlockCode)==api.World.Blocks[target].Code.ToString()))throw new InvalidOperationException("Container replacement requires retained matching metadata. Run select/results first; without metadata, use a backup or historical rollback.");
            StartInventoryRestorePreview(new(f.X,f.Y,f.Z,f.Dimension,"retained",0,!flags.Contains("no-backup"),flags.Contains("manual-copy-confirmed"),flags.Contains("confirm-risk")));
            return "Preparing retained inventory restoration, not a destructive generic replacement. /nseblockrepair results shows its preview ID; then /nseblockrepair restorestage ID. Applies on restart.";
        }
        PreviewSelected(new(f.X,f.Y,f.Z,f.Dimension,destination,flags.Contains("allow-air-source"),flags.Contains("confirm-risk"),!flags.Contains("no-backup"),flags.Contains("manual-copy-confirmed")));
        return "Preparing exactly one saved-location replacement. /nseblockrepair results shows the preview and ID; then /nseblockrepair selectedstage ID. Applies on restart.";
    }
    private TextCommandResult PlayerResult(TextCommandCallingArgs a,System.Func<IServerPlayer,string> action)
    {try{if(a.Caller.Player is not IServerPlayer p)throw new ArgumentException("Run this command in game.");string text=action(p);foreach(var line in text.Split('\n').Take(18))p.SendMessage(a.Caller.FromChatGroupId,BlockLogger.Commands.RollbackChatText.Encode(line),EnumChatType.CommandSuccess);return TextCommandResult.Success();}catch(Exception e){return TextCommandResult.Error(BlockLogger.Commands.RollbackChatText.Encode(e.Message.Replace('{','(').Replace('}',')')));}}
}
