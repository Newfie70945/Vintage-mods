using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;
using HarmonyLib;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService : IDisposable
{
    public sealed class Finding
    {
        public int Id {get;set;} public string Code {get;set;}="";public string Problem {get;set;}="";
        public long Solid {get;set;} public long Fluid {get;set;} public long Decor {get;set;}public long Chunks {get;set;}
        public List<string> Examples {get;set;}=[]; public long Count=>Solid+Fluid+Decor;
    }
    public sealed class Scan
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N");public string State {get;set;}="running";public string Message {get;set;}="";
        public long Chunks {get;set;}public long Errors {get;set;}public long Unsupported {get;set;}public int? FocusId {get;set;}
        public string Registry {get;set;}="";public List<Finding> Findings {get;set;}=[];public DateTime StartedUtc {get;set;}=DateTime.UtcNow;public DateTime? FinishedUtc {get;set;}
    }
    public sealed class Plan
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N"); public string World {get;set;}="";public string Registry {get;set;}="";public string ScanId {get;set;}="";
        public int Source {get;set;} public int Target {get;set;} public string SourceCode {get;set;}="";public string TargetCode {get;set;}="";
        public long Count {get;set;}public long Chunks {get;set;}public bool AllowAir {get;set;}public bool AllowValidSource {get;set;}public string State {get;set;}="preview";
        public bool CreateBackup {get;set;}=true;public bool ManualBackupAcknowledged {get;set;}
        public string Backup {get;set;}="";public string Message {get;set;}="";public long ChangedBlocks {get;set;}public long ChangedChunks {get;set;}
    }
    private readonly ICoreServerAPI api;private readonly object gate=new();private readonly string directory;private readonly string pendingFile;private readonly string historyFile;
    private Scan? scan;private Plan? preview;private Plan? pending;private Plan? last;private CancellationTokenSource? cancel;private Task? worker;private bool disposed;
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public BlockIdRepairService(ICoreServerAPI api){this.api=api;directory=api.GetOrCreateDataPath("NewfiesBlockRepair");pendingFile=Path.Combine(directory,"pending.json");historyFile=Path.Combine(directory,"last-repair.json");try{pending=Read<Plan>(pendingFile);last=Read<Plan>(historyFile);}catch(Exception e){last=new Plan{State="failed",Message="Repair state file could not be read: "+e.Message};api.Logger.Error("[BlockRepair] {0}",last.Message);}LoadInventoryRestore();RegisterCommands();}
    private static T? Read<T>(string path){if(!File.Exists(path))return default;return JsonSerializer.Deserialize<T>(File.ReadAllText(path));}
    private static void Write<T>(string path,T value){string temp=path+".tmp";using(var fs=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(fs,value,Json);fs.Flush(true);}File.Move(temp,path,true);}
    private GameDatabase Database(){if(api.World is not ServerMain server)throw new NotSupportedException("Official server required.");return (GameDatabase)AccessTools.Field(typeof(ChunkServerThread),"gameDatabase").GetValue(AccessTools.Field(typeof(ServerMain),"chunkThread").GetValue(server))!;}
    private void Guard()
    {
        if(disposed)throw new ObjectDisposedException(nameof(BlockIdRepairService));
        foreach(var method in typeof(ChunkDataLayer).GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static).Where(m=>m.Name is "Decompress" or "Compress" or "GetUnsafe"))
            if(Harmony.GetPatchInfo(method)?.Owners.Count>0)throw new NotSupportedException("Another mod patches the saved layer codec; repair is disabled to preserve its format.");
        if(api.World is not ServerMain||BlockLogger.Compatibility.RuntimeCompatibility.IsStratum)throw new NotSupportedException("Saved block repair supports the inspected official 1.22.7 server only.");
        string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ServerMain).Assembly.Location))).ToLowerInvariant();
        if(sha!="e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0")throw new NotSupportedException("Saved chunk codec differs from the inspected official 1.22.7 build; no world changes made.");
    }
    private Dictionary<int,string> Mapping()
    {
        byte[]? data=api.WorldManager.SaveGame.GetData("BlockIDs");if(data==null)throw new InvalidOperationException("World block-ID mapping is not ready.");
        using var stream=new MemoryStream(data);return Serializer.Deserialize<Dictionary<int,string>>(stream);
    }
    private string Fingerprint(Dictionary<int,string> map)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map.OrderBy(x=>x.Key)))));
    private bool Missing(int id)=>id<0||id>=api.World.Blocks.Count||api.World.Blocks[id]==null||api.World.Blocks[id].Code==null||api.World.Blocks[id].IsMissing;
    private string Label(int id,Dictionary<int,string> map)=>map.TryGetValue(id,out var code)?code:id>=0&&id<api.World.Blocks.Count?api.World.Blocks[id]?.Code?.ToString()??"":"";
    public object Snapshot(bool includeInspection=true)
    {
        lock(gate){var map=Mapping();return new{world=api.World.SavegameIdentifier,scan,preview,pending,last,inspection=includeInspection?inspection:null,recommendations=includeInspection?recommendations:null,restorePreview=includeInspection?restorePreview:null,restorePending=includeInspection?restorePending:null,restoreLast=includeInspection?restoreLast:null,
            missingMappings=map.Where(x=>x.Key!=0&&Missing(x.Key)).Take(4096).Select(x=>new{id=x.Key,code=x.Value,problem="Missing block definition; saved occurrence count requires a scan."}).ToArray(),
            scope="Solid, fluid and decor IDs in existing saved chunks. Unsaved live edits are not included. Preview/stage never changes terrain; a staged repair applies before terrain loads on restart.",
            limitations="Replacement blocks must have no block-entity class. A source hosting a block entity is refused. Lighting/heightmap caches are preserved; relight and refresh map exports if needed."};}
    }
    public object StartScan(int? focus)
    {
        Guard();lock(gate){if(worker is{IsCompleted:false})throw new InvalidOperationException("A scan is already running.");if(pending!=null)throw new InvalidOperationException("Cancel the staged repair before scanning again.");
            if(focus==0)throw new ArgumentException("Air ID 0 cannot be a source.");var map=Mapping();var missing=api.World.Blocks.Select((b,i)=>b==null||b.Code==null||b.IsMissing).ToArray();string registry=Fingerprint(map),path=Database().DatabaseFilename;
            cancel?.Dispose();cancel=new();var token=cancel.Token;scan=new(){FocusId=focus,Registry=registry};preview=null;var current=scan;var server=(ServerMain)api.World;
            worker=Task.Run(()=>ScanSaved(current,path,map,missing,server,token));return new{success=true,scanId=current.Id};}
    }
    private static SqliteConnection Open(string path){var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());connection.Open();return connection;}
    private static List<(long Key,byte[] Data)> Page(string path,long? after)
    {
        using var connection=Open(path);using var cmd=connection.CreateCommand();cmd.CommandTimeout=2;cmd.CommandText=after.HasValue?"SELECT position,data FROM chunk WHERE position>$after ORDER BY position LIMIT 16":"SELECT position,data FROM chunk ORDER BY position LIMIT 16";if(after.HasValue)cmd.Parameters.AddWithValue("$after",after.Value);
        using var reader=cmd.ExecuteReader();var rows=new List<(long,byte[])>();long pageBytes=0;while(reader.Read()){long length=reader.GetBytes(1,0,null,0,0);if(length>64*1024*1024)throw new InvalidDataException("Oversized saved chunk.");if(rows.Count>0&&pageBytes+length>8*1024*1024)break;pageBytes+=length;rows.Add((reader.GetInt64(0),(byte[])reader[1]));}return rows;
    }
    private void ScanSaved(Scan current,string path,Dictionary<int,string> map,bool[] missing,ServerMain server,CancellationToken token)
    {
        var pool=new ChunkDataPool(32,server){CacheSize=32};var codec=new BlockIdChunkCodec(pool);var found=new Dictionary<int,Finding>();long? cursor=null;long scanned=0,errors=0,unsupported=0;
        try{while(true){token.ThrowIfCancellationRequested();var rows=Page(path,cursor);if(rows.Count==0)break;foreach(var row in rows){token.ThrowIfCancellationRequested();cursor=row.Key;scanned++;
                try{var counts=codec.Inspect(row.Data,id=>id!=0&&(id==current.FocusId||id<0||id>=missing.Length||missing[id]));foreach(var item in counts){if(!found.TryGetValue(item.Key,out var finding)){if(found.Count>=4096)throw new InvalidDataException("Too many distinct unresolved IDs.");found[item.Key]=finding=new(){Id=item.Key,Code=map.GetValueOrDefault(item.Key,""),Problem=item.Key>=0&&item.Key<missing.Length&&!missing[item.Key]?"Selected registered ID":"Missing/unregistered block ID"};}
                        finding.Solid+=item.Value[0];finding.Fluid+=item.Value[1];finding.Decor+=item.Value[2];finding.Chunks++;if(finding.Examples.Count<5){var pos=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));finding.Examples.Add($"chunk {pos.X}, {pos.Y}, {pos.Z}; key {row.Key}");}}
                }catch(NotSupportedException){unsupported++;}catch{errors++;}Thread.Sleep(2);}
                lock(gate){current.Chunks=scanned;current.Errors=errors;current.Unsupported=unsupported;current.Findings=found.Values.OrderByDescending(x=>x.Count).Select(Clone).ToList();}
            }
            lock(gate){current.State="complete";current.Message="Saved-map scan finished. Counts describe the chunks read; live unsaved edits and concurrent saves can change them.";}
        }catch(OperationCanceledException){lock(gate){current.State="cancelled";current.Message="Scan cancelled; no terrain was changed.";}}
        catch(Exception e){lock(gate){current.State="failed";current.Message="Scan failed: "+e.Message;}}
        finally{lock(gate){current.Chunks=scanned;current.Errors=errors;current.Unsupported=unsupported;current.Findings=found.Values.OrderByDescending(x=>x.Count).Select(Clone).ToList();current.FinishedUtc=DateTime.UtcNow;}pool.FreeAll();}
    }
    private static Finding Clone(Finding f)=>new(){Id=f.Id,Code=f.Code,Problem=f.Problem,Solid=f.Solid,Fluid=f.Fluid,Decor=f.Decor,Chunks=f.Chunks,Examples=f.Examples.ToList()};
    private void Validate(Plan p)
    {
        if(!SavedBlockEntityInspector.BuiltinTypesSupported())throw new NotSupportedException("A mod changed built-in saved attribute types; repair is disabled to protect retained block entities.");
        if(!p.CreateBackup&&!p.ManualBackupAcknowledged)throw new InvalidOperationException("Automatic backup is disabled. Make a manual copy of your world and acknowledge it before previewing or staging a repair.");
        if(p.Source==0||p.Source==p.Target||p.Target<0)throw new ArgumentException("Use a non-air source and a different valid destination ID.");
        if(p.World!=api.World.SavegameIdentifier||p.Registry!=Fingerprint(Mapping()))throw new InvalidOperationException("World or block-ID mapping changed; make a fresh scan and preview.");
        if(!p.AllowValidSource&&!Missing(p.Source))throw new InvalidOperationException("Source is a registered block. Explicitly allow replacing a valid ID to continue.");
        if(p.Target==0&&!p.AllowAir)throw new InvalidOperationException("Replacing with air requires the explicit allow-air option.");
        if(Missing(p.Target))throw new ArgumentException("Destination is missing or unregistered.");var target=api.World.Blocks[p.Target];if(p.TargetCode!=target.Code.ToString())throw new InvalidOperationException("Destination code no longer matches its reviewed numeric ID; create a fresh preview.");if(!string.IsNullOrEmpty(target.EntityClass))throw new ArgumentException("Destination requires a block entity; use a dedicated data migration instead.");
    }
    private int ResolveTarget(string destination)
    {
        destination=destination.Trim();
        if(int.TryParse(destination,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out int id))return id;
        if(destination.Length==0||destination.Length>256||!destination.Contains(':')||destination.Any(c=>char.IsWhiteSpace(c)||c is '*' or '?'))throw new ArgumentException("Enter a numeric destination ID or an exact block code such as game:rock-granite.");
        var block=api.World.GetBlock(new AssetLocation(destination));
        if(block==null||block.IsMissing||block.Code?.ToString()!=destination)throw new ArgumentException("Destination block code is not registered. Use the exact domain:code, including its variant.");
        return block.BlockId;
    }
    public Plan Preview(int source,string destination,bool allowAir,bool allowValid,bool createBackup=true,bool manualBackupAcknowledged=false)
    {
        Guard();lock(gate){int target=ResolveTarget(destination);if(pending!=null)throw new InvalidOperationException("Cancel the staged repair first.");if(scan?.State!="complete"||scan.Errors!=0||scan.Unsupported!=0)throw new InvalidOperationException("A complete, error-free saved-map scan is required. Legacy chunks must be converted first.");
            var match=scan.Findings.SingleOrDefault(x=>x.Id==source);if(match==null||match.Count==0)throw new InvalidOperationException("This ID has no scanned occurrences. For a registered source, scan with its numeric ID selected.");var map=Mapping();
            var plan=new Plan{Source=source,Target=target,AllowAir=allowAir,AllowValidSource=allowValid,CreateBackup=createBackup,ManualBackupAcknowledged=manualBackupAcknowledged,World=api.World.SavegameIdentifier,Registry=scan.Registry,ScanId=scan.Id,Count=match.Count,Chunks=match.Chunks,SourceCode=Label(source,map),TargetCode=Missing(target)?"":api.World.Blocks[target].Code.ToString()};Validate(plan);
            var block=api.World.Blocks[target];if(target!=0&&((match.Fluid>0&&!block.ForFluidsLayer)||((match.Solid>0||match.Decor>0)&&block.ForFluidsLayer)))throw new ArgumentException("Destination does not match the affected solid/fluid layer. Air may be used explicitly for removal.");
            plan.Message="Preview only. Destination resolved to "+plan.TargetCode+" (ID "+target+"). "+(createBackup?"A verified automatic world backup will be created before edits.":"AUTOMATIC BACKUP DISABLED. Your manual copy is the recovery path; the Suite has not verified it.")+" Stage to repair saved terrain on the next restart.";preview=plan;return plan;}
    }
    public Plan Stage(string id,int? expectedSource=null,string? expectedTarget=null,bool? expectedBackup=null,bool? expectedManual=null,bool? expectedAir=null,bool? expectedValid=null)
    {
        Guard();lock(gate){if(restorePending!=null)throw new InvalidOperationException("An inventory restore is already staged.");if(preview==null||preview.Id!=id)throw new InvalidOperationException("Preview expired or missing.");if((expectedSource.HasValue&&expectedSource!=preview.Source)||(expectedTarget!=null&&ResolveTarget(expectedTarget)!=preview.Target)||(expectedBackup.HasValue&&expectedBackup!=preview.CreateBackup)||(expectedManual.HasValue&&expectedManual!=preview.ManualBackupAcknowledged)||(expectedAir.HasValue&&expectedAir!=preview.AllowAir)||(expectedValid.HasValue&&expectedValid!=preview.AllowValidSource))throw new InvalidOperationException("Inputs changed; generate a fresh preview.");Validate(preview);preview.State="pending";Write(pendingFile,preview);pending=preview;preview=null;return pending;}
    }
    public object Cancel()
    {
        lock(gate){cancel?.Cancel();restorePreview=null;if(File.Exists(RestorePendingFile))File.Delete(RestorePendingFile);restorePending=null;preview=null;if(File.Exists(pendingFile))File.Delete(pendingFile);pending=null;return new{success=true,message="Scan cancellation requested; staged repair removed. No terrain changed."};}
    }
    public void ApplyBeforeTerrainLoads()
    {
        if(restorePending!=null){ApplyInventoryRestoreBeforeTerrainLoads();return;}
        if(pending==null)return;var p=pending;var db=Database();bool writes=false;ChunkDataPool? pool=null;
        try{Guard();Validate(p);var server=(ServerMain)api.World;var thread=AccessTools.Field(typeof(ServerMain),"chunkThread").GetValue(server)!;
            // Official workers exist at this point, but SupplyChunks only processes
            // terrain in RunGame and the save worker requires its pending flag.
            // The owning main thread holds this phase until maintenance returns.
            if(api.Server.CurrentRunPhase!=EnumServerRunPhase.AssetsFinalize||((ChunkServerThread)thread).runOffThreadSaveNow)throw new InvalidOperationException("Repair requires AssetsFinalize with no pending background save.");
            int Queued()=>Convert.ToInt32(AccessTools.Property(AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns").GetValue(thread)!.GetType(),"Count").GetValue(AccessTools.Field(typeof(ChunkServerThread),"requestedChunkColumns").GetValue(thread)));
            if(api.WorldManager.AllLoadedChunks.Count>0||Queued()>0)throw new InvalidOperationException("Terrain is already loaded or queued; repair cannot run safely at this startup stage.");
            p.State="preflight";p.Message="Validating every saved occurrence before backup or edits.";Write(historyFile,p);
            pool=new ChunkDataPool(32,server){CacheSize=32};var codec=new BlockIdChunkCodec(pool);long? cursor=null;long reviewed=0;
            // Full preflight avoids partial mutation when a source hosts block entities,
            // a legacy/corrupt chunk exists, or a layer mismatch was introduced later.
            while(true){var rows=Page(db.DatabaseFilename,cursor);if(rows.Count==0)break;foreach(var row in rows){cursor=row.Key;if(++reviewed%1024==0)api.Logger.Notification("[BlockRepair] Preflight: {0} saved chunks checked.",reviewed);var counts=codec.Inspect(row.Data,id=>id==p.Source);if(counts.TryGetValue(p.Source,out var c)){var target=api.World.Blocks[p.Target];if(p.Target!=0&&((c[1]>0&&!target.ForFluidsLayer)||((c[0]+c[2]>0)&&target.ForFluidsLayer)))throw new InvalidOperationException("Affected block layer changed since preview.");codec.Replace(row.Data,p.Source,p.Target,out _);}}}
            if(p.CreateBackup)
            {
                p.Backup=Path.Combine(directory,"world-before-"+p.Id+".vcdbs");api.Logger.Notification("[BlockRepair] Creating backup before repair {0} -> {1}: {2}",p.Source,p.Target,p.Backup);db.CreateBackup(p.Backup);
                using var backup=Open(p.Backup);using var check=backup.CreateCommand();check.CommandTimeout=600;check.CommandText="PRAGMA quick_check";if(!string.Equals(Convert.ToString(check.ExecuteScalar()),"ok",StringComparison.Ordinal))throw new InvalidDataException("Backup integrity check failed.");
            }
            else
            {
                p.Backup="";api.Logger.Warning("[BlockRepair] AUTOMATIC BACKUP DISABLED for plan {0}. Owner acknowledged a manual world copy; the Suite has not verified it.",p.Id);
            }
            p.State="applying";p.Message=(p.CreateBackup?"Backup verified.":"Automatic backup disabled; manual world copy acknowledged, not verified.")+" Repairing existing saved chunks before terrain loads.";Write(historyFile,p);
            // Consume before the first edit. A crash never silently replays a job.
            File.Move(pendingFile,Path.Combine(directory,"consumed-"+p.Id+".json"),true);pending=null;cursor=null;
            while(true){var rows=Page(db.DatabaseFilename,cursor);if(rows.Count==0)break;var changes=new List<DbChunk>();long batchBlocks=0;foreach(var row in rows){cursor=row.Key;byte[] bytes=codec.Replace(row.Data,p.Source,p.Target,out long changed);if(changed==0)continue;changes.Add(new DbChunk(ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key)),bytes));batchBlocks+=changed;}
                if(changes.Count>0){if(api.Server.CurrentRunPhase!=EnumServerRunPhase.AssetsFinalize||((ChunkServerThread)thread).runOffThreadSaveNow||api.WorldManager.AllLoadedChunks.Count>0||Queued()>0)throw new InvalidOperationException("Another mod started terrain or saving during repair.");writes=true;db.SetChunks(changes);p.ChangedChunks+=changes.Count;p.ChangedBlocks+=batchBlocks;Write(historyFile,p);}
            }
            p.State="complete";p.Message="Saved-map repair complete. "+(p.CreateBackup?"Automatic backup retained.":"No automatic backup was made; retain your manual copy.")+" Cached lighting/heightmaps and external map exports may need refreshing.";
            api.Logger.Notification("[BlockRepair] Completed {0} -> {1}: {2} entries in {3} chunks. Backup: {4}",p.Source,p.Target,p.ChangedBlocks,p.ChangedChunks,p.Backup);
        }catch(Exception e){p.State=writes?"failed-partial":"failed";p.Message=e.Message+(writes?(p.CreateBackup?" Some chunks changed. Restore the retained world backup before retrying.":" Some chunks changed. No automatic backup exists. Restore your manual world copy before retrying."):" No terrain edits were made.");api.Logger.Error("[BlockRepair] {0}",p.Message);if(writes)((ServerMain)api.World).Stop("Block repair partially failed; restore backup before continuing",EnumExitMode.SoftExit);if(File.Exists(pendingFile))File.Move(pendingFile,Path.Combine(directory,"rejected-"+p.Id+".json"),true);pending=null;}
        finally{pool?.FreeAll();last=p;Write(historyFile,p);}
    }
    private void RegisterCommands()
    {
        var parsers=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseblockrepair").RequiresPrivilege(Privilege.controlserver).WithDescription("Saved-map numeric block ID inspection and staged repair")
            .BeginSubCommand("status").HandleWith(_=>Run(()=>Snapshot())).EndSubCommand()
            .BeginSubCommand("scan").WithArgs(parsers.OptionalInt("source ID")).HandleWith(a=>Run(()=>StartScan(a.Parsers[0].IsMissing?null:(int)a[0]))).EndSubCommand()
            .BeginSubCommand("inspect").WithArgs(parsers.Int("absolute X"),parsers.Int("Y"),parsers.Int("absolute Z"),parsers.OptionalInt("dimension")).HandleWith(a=>Run(()=>StartInspection((int)a[0],(int)a[1],(int)a[2],a.Parsers[3].IsMissing?0:(int)a[3]))).EndSubCommand()
            .BeginSubCommand("inspectchunk").WithArgs(parsers.Word("saved chunk key")).HandleWith(a=>Run(()=>StartInspection(0,0,0,0,(string)a[0]))).EndSubCommand()
            .BeginSubCommand("recommend").WithArgs(parsers.Int("absolute X"),parsers.Int("Y"),parsers.Int("absolute Z"),parsers.OptionalInt("dimension")).HandleWith(a=>Run(()=>StartRecommendations((int)a[0],(int)a[1],(int)a[2],a.Parsers[3].IsMissing?0:(int)a[3]))).EndSubCommand()
            .BeginSubCommand("search").WithArgs(parsers.Word("block code or ID")).HandleWith(a=>Run(()=>SearchTargets((string)a[0]))).EndSubCommand()
            .BeginSubCommand("restorepreview").WithArgs(parsers.Int("absolute X"),parsers.Int("Y"),parsers.Int("absolute Z"),parsers.Int("dimension"),parsers.Word("retained or snapshot"),parsers.Word("snapshot ID or 0"),parsers.OptionalAll("options")).HandleWith(a=>Run(()=>StartInventoryRestorePreview(RestoreCommandRequest((int)a[0],(int)a[1],(int)a[2],(int)a[3],(string)a[4],(string)a[5],a[6] as string??"")))).EndSubCommand()
            .BeginSubCommand("restorestage").WithArgs(parsers.Word("restore preview ID")).HandleWith(a=>Run(()=>StageInventoryRestore((string)a[0]))).EndSubCommand()
            .BeginSubCommand("preview").WithArgs(parsers.Int("old ID"),parsers.Word("destination ID or code"),parsers.OptionalAll("options")).HandleWith(a=>Run(()=>PreviewCommand((int)a[0],(string)a[1],a[2] as string??""))).EndSubCommand()
            .BeginSubCommand("stage").WithArgs(parsers.Word("preview ID")).HandleWith(a=>Run(()=>Stage((string)a[0]))).EndSubCommand()
            .BeginSubCommand("cancel").HandleWith(_=>Run(Cancel)).EndSubCommand();
    }
    private Plan PreviewCommand(int source,string destination,string options)
    {
        var flags=options.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if(flags.Any(x=>x is not ("allow-air" or "allow-valid" or "no-backup" or "manual-copy-confirmed" or "-")))throw new ArgumentException("Options: allow-air, allow-valid, no-backup manual-copy-confirmed. Make a manual world copy before disabling automatic backup.");
        return Preview(source,destination,flags.Contains("allow-air"),flags.Contains("allow-valid"),!flags.Contains("no-backup"),flags.Contains("manual-copy-confirmed"));
    }
    private static InventoryRestoreRequest RestoreCommandRequest(int x,int y,int z,int dim,string mode,string snapshot,string options)
    {
        if(!long.TryParse(snapshot,out long id))throw new ArgumentException("Use the historical snapshot ID, or 0 for retained data.");
        var flags=options.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if(flags.Any(f=>f is not ("confirm-risk" or "no-backup" or "manual-copy-confirmed")))throw new ArgumentException("Options: confirm-risk, no-backup manual-copy-confirmed.");
        return new(x,y,z,dim,mode,id,!flags.Contains("no-backup"),flags.Contains("manual-copy-confirmed"),flags.Contains("confirm-risk"));
    }
    private TextCommandResult Run(Func<object> fn){try{var result=fn();api.Logger.Notification("{0}","NSE_BLOCKREPAIR_RESULT "+JsonSerializer.Serialize(result));string message=result is InventoryRestorePlan rp?$"Inventory restore {rp.Id}: {rp.State}; {rp.Items} items at one location.":result is Plan p?$"Plan {p.Id}: ID {p.Source} -> {p.Target}; {p.Count} scanned entries; {p.State}.":$"Block repair: scan {scan?.State??"none"}, chunks {scan?.Chunks??0}, errors {scan?.Errors??0}. Inspection {inspection?.State??"none"}. Full result printed to console and available in the webpanel.";return TextCommandResult.Success(BlockLogger.Commands.RollbackChatText.Encode(message));}catch(Exception e){return TextCommandResult.Error(BlockLogger.Commands.RollbackChatText.Encode(e.Message.Replace('{','(').Replace('}',')')));}}
    public void Dispose(){lock(gate){disposed=true;cancel?.Cancel();}try{worker?.Wait(3000);}catch{}if(worker?.IsCompleted!=false)cancel?.Dispose();}
}
