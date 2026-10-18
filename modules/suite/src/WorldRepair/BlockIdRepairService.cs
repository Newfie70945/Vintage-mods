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
    public sealed record SamplePosition(int X,int Y,int Z,int Dimension);
    public sealed class Finding
    {
        public int Id {get;set;} public string Code {get;set;}="";public string Problem {get;set;}="";
        public string Category {get;set;}="unresolved-id";public string CurrentCode {get;set;}="";public int? SameCodeRegisteredId {get;set;}
        public long Solid {get;set;} public long Fluid {get;set;} public long Decor {get;set;}public long Chunks {get;set;}
        public List<SamplePosition> Positions {get;set;}=[];public List<string> Examples {get;set;}=[]; public long Count=>Solid+Fluid+Decor;
    }
    public sealed class Scan
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N");public string State {get;set;}="running";public string Message {get;set;}="";
        public string Mode {get;set;}="missing-blocks";public long Chunks {get;set;}public long Errors {get;set;}public long Unsupported {get;set;}public int? FocusId {get;set;}
        [System.Text.Json.Serialization.JsonIgnore] public List<RetainedBlockFindings.Finding> RetainedContainers {get;set;}=[];public long RetainedContainerCount {get;set;}public long UnverifiedContainers {get;set;}
        public string Registry {get;set;}="";public List<Finding> Findings {get;set;}=[];public DateTime StartedUtc {get;set;}=DateTime.UtcNow;public DateTime? FinishedUtc {get;set;}
    }
    public sealed class RepairMapping
    {
        public int Source {get;set;}public int Target {get;set;}public string SourceCode {get;set;}="";public string TargetCode {get;set;}="";public long Count {get;set;}public long Chunks {get;set;}
    }
    public sealed class Plan
    {
        public string Id {get;set;}=Guid.NewGuid().ToString("N"); public string World {get;set;}="";public string Registry {get;set;}="";public string ScanId {get;set;}="";
        public List<RepairMapping> Mappings {get;set;}=[];
        public int Source {get;set;} public int Target {get;set;} public string SourceCode {get;set;}="";public string TargetCode {get;set;}="";
        public long Count {get;set;}public long Chunks {get;set;}public bool AllowAir {get;set;}public bool AllowValidSource {get;set;}public string State {get;set;}="preview";
        public bool CreateBackup {get;set;}=true;public bool ManualBackupAcknowledged {get;set;}
        public string Backup {get;set;}="";public string Message {get;set;}="";public long ChangedBlocks {get;set;}public long ChangedChunks {get;set;}
    }
    private readonly ICoreServerAPI api;private readonly object gate=new();private readonly string directory;private readonly string pendingFile;private readonly string historyFile;
    private Scan? scan;private Plan? preview;private Plan? pending;private Plan? last;private CancellationTokenSource? cancel;private Task? worker;private bool disposed;
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public BlockIdRepairService(ICoreServerAPI api){this.api=api;directory=api.GetOrCreateDataPath("NewfiesBlockRepair");pendingFile=Path.Combine(directory,"pending.json");historyFile=Path.Combine(directory,"last-repair.json");try{pending=Read<Plan>(pendingFile);last=Read<Plan>(historyFile);}catch(Exception e){last=new Plan{State="failed",Message="Repair state file could not be read: "+e.Message};api.Logger.Error("[BlockRepair] {0}",last.Message);}LoadInventoryRestore();LoadSelectedRepair();RegisterCommands();}
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
        lock(gate){var map=Mapping();return new{world=api.World.SavegameIdentifier,scan,preview,pending,last,inspection=includeInspection?inspection:null,recommendations=includeInspection?recommendations:null,mappingAdvice=includeInspection?mappingAdvice:null,restorePreview=includeInspection?restorePreview:null,restorePending=includeInspection?restorePending:null,restoreLast=includeInspection?restoreLast:null,selectedFocus=includeInspection?selectedFocus:null,selectedPreview=includeInspection?selectedPreview:null,selectedPending=includeInspection?selectedPending:null,selectedLast=includeInspection?selectedLast:null,retainedContainers=includeInspection?scan?.RetainedContainers:null,
            missingMappings=map.Where(x=>x.Key>0&&x.Key<api.World.Blocks.Count&&MissingBlockScan.IsPlaceholder(api.World.Blocks[x.Key])).Take(4096).Select(x=>new{id=x.Key,code=x.Value,problem="Missing-block (?) placeholder; saved occurrence count requires a scan."}).ToArray(),
            scope="Default scan: missing-block (?) placeholders only. Advanced diagnostics additionally investigate unresolved IDs, registered code mismatches and retained block entities. Solid, fluid and decor IDs in existing saved chunks. Unsaved live edits are not included. Preview/stage never changes terrain; a staged repair applies before terrain loads on restart.",
            limitations="Replacement blocks must have no block-entity class. A source hosting a block entity is refused. Counts cover saved occurrences, including underground/unloaded placeholders, not a player-visible count. Missing client textures and plants already saved as air cannot be detected by the default scan. Lighting/heightmap caches are preserved; relight and refresh map exports if needed."};}
    }
    public object StartScan(int? focus,bool diagnostics=false)
    {
        Guard();lock(gate){if(worker is{IsCompleted:false})throw new InvalidOperationException("A scan is already running.");if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Cancel the staged repair before scanning again.");
            if(focus==0)throw new ArgumentException("Air ID 0 cannot be a source.");var map=Mapping();var placeholders=api.World.Blocks.Select(MissingBlockScan.IsPlaceholder).ToArray();if(!diagnostics&&focus.HasValue&&(focus.Value<0||focus.Value>=placeholders.Length||!placeholders[focus.Value]))throw new ArgumentException("This ID is not a missing-block (?) placeholder. Use Advanced Diagnostic Scan to investigate registered IDs or unresolved raw IDs.");var unresolved=api.World.Blocks.Select(b=>b==null||b.Code==null||b.IsMissing).ToArray();var missing=api.World.Blocks.Select((b,i)=>unresolved[i]||RetainedBlockFindings.MappingMismatch(i,map.GetValueOrDefault(i),b?.Code?.ToString())).ToArray();var available=new Dictionary<int,int>();foreach(var entry in map.Where(e=>e.Key!=0&&(e.Key<0||e.Key>=missing.Length||missing[e.Key]))){var block=api.World.GetBlock(new AssetLocation(entry.Value));if(block?.Code!=null&&!block.IsMissing)available[entry.Key]=block.BlockId;}var codes=api.World.Blocks.Select(b=>b?.Code?.ToString()??"").ToArray();var classes=api.World.Blocks.Select(b=>b?.EntityClass??"").ToArray();string registry=Fingerprint(map),path=Database().DatabaseFilename;
            cancel?.Dispose();cancel=new();var token=cancel.Token;mappingAdvice=null;scan=new(){FocusId=focus,Mode=diagnostics?"diagnostics":"missing-blocks",Registry=registry};preview=null;var current=scan;var server=(ServerMain)api.World;
            worker=Task.Run(()=>ScanSaved(current,path,map,missing,unresolved,placeholders,available,codes,classes,server,token));return new{success=true,scanId=current.Id};}
    }
    private static SqliteConnection Open(string path){var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());connection.Open();return connection;}
    private static List<(long Key,byte[] Data)> Page(string path,long? after)
    {
        using var connection=Open(path);using var cmd=connection.CreateCommand();cmd.CommandTimeout=2;cmd.CommandText=after.HasValue?"SELECT position,data FROM chunk WHERE position>$after ORDER BY position LIMIT 16":"SELECT position,data FROM chunk ORDER BY position LIMIT 16";if(after.HasValue)cmd.Parameters.AddWithValue("$after",after.Value);
        using var reader=cmd.ExecuteReader();var rows=new List<(long,byte[])>();long pageBytes=0;while(reader.Read()){long length=reader.GetBytes(1,0,null,0,0);if(length>64*1024*1024)throw new InvalidDataException("Oversized saved chunk.");if(rows.Count>0&&pageBytes+length>8*1024*1024)break;pageBytes+=length;rows.Add((reader.GetInt64(0),(byte[])reader[1]));}return rows;
    }
    private void ScanSaved(Scan current,string path,Dictionary<int,string> map,bool[] missing,bool[] unresolved,bool[] placeholders,Dictionary<int,int> available,string[] codes,string[] classes,ServerMain server,CancellationToken token)
    {
        var pool=new ChunkDataPool(32,server){CacheSize=32};var codec=new BlockIdChunkCodec(pool);var found=new Dictionary<int,Finding>();var samples=new Dictionary<int,List<SamplePosition>>();long? cursor=null;long scanned=0,errors=0,unsupported=0;
        try{while(true){token.ThrowIfCancellationRequested();var rows=Page(path,cursor);if(rows.Count==0)break;foreach(var row in rows){token.ThrowIfCancellationRequested();cursor=row.Key;scanned++;
                try{var sampleChunk=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));if(current.Mode=="diagnostics"){var orphaned=RetainedBlockFindings.Inspect(row.Data,codec,id=>id>=0&&id<codes.Length?codes[id]:"",id=>id>=0&&id<classes.Length?classes[id]:"",out int unverified);var chunk=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));orphaned=orphaned.Where(e=>e.X/32==chunk.X&&e.Y/32==chunk.Y&&e.Z/32==chunk.Z&&e.Dimension==chunk.Dimension&&(!current.FocusId.HasValue||e.SolidId==current.FocusId.Value)).ToList();lock(gate){current.UnverifiedContainers+=unverified;current.RetainedContainerCount+=orphaned.Count;current.RetainedContainers.AddRange(orphaned.Take(Math.Max(0,256-current.RetainedContainers.Count)));}}
                    var counts=codec.Inspect(row.Data,id=>MissingBlockScan.Includes(id,current.FocusId,current.Mode=="diagnostics",placeholders,missing),(id,layer,index)=>{if(layer!=0)return;if(!samples.TryGetValue(id,out var positions)){if(samples.Count>=4096)return;samples[id]=positions=[];}if(positions.Count<2&&!positions.Any(p=>p.X/32==sampleChunk.X&&p.Y/32==sampleChunk.Y&&p.Z/32==sampleChunk.Z&&p.Dimension==sampleChunk.Dimension))positions.Add(new(sampleChunk.X*32+(index&31),sampleChunk.Y*32+(index>>10),sampleChunk.Z*32+((index>>5)&31),sampleChunk.Dimension));});foreach(var item in counts){if(!found.TryGetValue(item.Key,out var finding)){if(found.Count>=4096)throw new InvalidDataException("Too many distinct unresolved IDs.");found[item.Key]=finding=new(){Id=item.Key,Code=map.GetValueOrDefault(item.Key,""),CurrentCode=item.Key>=0&&item.Key<codes.Length?codes[item.Key]:"",SameCodeRegisteredId=available.TryGetValue(item.Key,out int registeredId)?registeredId:null,Category=item.Key>=0&&item.Key<placeholders.Length&&placeholders[item.Key]?"missing-placeholder":item.Key>=0&&item.Key<unresolved.Length&&!unresolved[item.Key]?(missing[item.Key]?"registered-mismatch":"selected-registered"):"unresolved-id",Problem=item.Key>=0&&item.Key<placeholders.Length&&placeholders[item.Key]?"Missing-block (?) placeholder":item.Key>=0&&item.Key<unresolved.Length&&!unresolved[item.Key]?(missing[item.Key]?"Saved/current code mismatch; current block is registered (review required)":"Selected registered ID; not a missing-block finding"):"Unresolved saved ID; visible damage not verified"};}
                        finding.Positions=samples.GetValueOrDefault(item.Key,[]);finding.Solid+=item.Value[0];finding.Fluid+=item.Value[1];finding.Decor+=item.Value[2];finding.Chunks++;if(finding.Examples.Count<5){var pos=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));finding.Examples.Add($"chunk {pos.X}, {pos.Y}, {pos.Z}; key {row.Key}");}}
                }catch(NotSupportedException){unsupported++;}catch{errors++;}Thread.Sleep(2);}
                lock(gate){current.Chunks=scanned;current.Errors=errors;current.Unsupported=unsupported;current.Findings=found.Values.OrderByDescending(x=>x.Count).Select(Clone).ToList();}
            }
            lock(gate){current.State="complete";current.Message=(current.Mode=="diagnostics"?"Advanced diagnostic scan finished. These findings are not all question-mark blocks. Review retained block entities separately.":"Missing-block (?) scan finished. Only coded IsMissing placeholders were counted; registered blocks, undefined slots and retained-container diagnostics were excluded.")+" Counts include underground/unloaded saved occurrences. Per-ID chunk counts overlap. Plants already saved as air require exact-location history/backup evidence; air is not globally scanned as damage. Concurrent saves can change counts.";}
        }catch(OperationCanceledException){lock(gate){current.State="cancelled";current.Message="Scan cancelled; no terrain was changed.";}}
        catch(Exception e){lock(gate){current.State="failed";current.Message="Scan failed: "+e.Message;}}
        finally{lock(gate){current.Chunks=scanned;current.Errors=errors;current.Unsupported=unsupported;current.Findings=found.Values.OrderByDescending(x=>x.Count).Select(Clone).ToList();current.FinishedUtc=DateTime.UtcNow;}pool.FreeAll();}
    }
    private static Finding Clone(Finding f)=>new(){Id=f.Id,Code=f.Code,Problem=f.Problem,Solid=f.Solid,Fluid=f.Fluid,Decor=f.Decor,Chunks=f.Chunks,Examples=f.Examples.ToList(),Category=f.Category,CurrentCode=f.CurrentCode,SameCodeRegisteredId=f.SameCodeRegisteredId,Positions=f.Positions.ToList()};
    private static List<RepairMapping> EffectiveMappings(Plan p)=>p.Mappings.Count>0?p.Mappings:[new(){Source=p.Source,Target=p.Target,SourceCode=p.SourceCode,TargetCode=p.TargetCode,Count=p.Count,Chunks=p.Chunks}];
    private void Validate(Plan p)
    {
        if(!SavedBlockEntityInspector.BuiltinTypesSupported())throw new NotSupportedException("A mod changed built-in saved attribute types; repair is disabled to protect retained block entities.");
        if(!p.CreateBackup&&!p.ManualBackupAcknowledged)throw new InvalidOperationException("Automatic backup is disabled. Make a manual copy of your world and acknowledge it before previewing or staging a repair.");
        if(p.World!=api.World.SavegameIdentifier||p.Registry!=Fingerprint(Mapping()))throw new InvalidOperationException("World or block-ID mapping changed; make a fresh scan and preview.");
        var mappings=EffectiveMappings(p);RemapSelection.Resolve(mappings.Select(m=>new RemapChoice(m.Source,m.Target.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(),int.Parse);
        foreach(var m in mappings)
        {
            if(!p.AllowValidSource&&!Missing(m.Source))throw new InvalidOperationException("Source is a registered block. Explicitly allow replacing a valid ID to continue.");
            if(m.Target==0&&!p.AllowAir)throw new InvalidOperationException("Replacing with air requires the explicit allow-air option.");
            if(Missing(m.Target))throw new ArgumentException("Destination is missing or unregistered.");var target=api.World.Blocks[m.Target];
            if(m.TargetCode!=target.Code.ToString())throw new InvalidOperationException("Destination code no longer matches its reviewed numeric ID; create a fresh preview.");
            if(!string.IsNullOrEmpty(target.EntityClass))throw new ArgumentException("Destination requires a block entity; use retained inventory recovery instead of a global ID remap.");
            if(p.Mappings.Count>0&&m.Count<=0)throw new ArgumentException("Every selected mapping must have scanned occurrences.");
        }
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
        =>PreviewMappings([new(source,destination)],allowAir,allowValid,createBackup,manualBackupAcknowledged,false);
    public Plan PreviewBatch(IReadOnlyList<RemapChoice> choices,bool allowAir,bool allowValid,bool createBackup=true,bool manualBackupAcknowledged=false)
        =>PreviewMappings(choices,allowAir,allowValid,createBackup,manualBackupAcknowledged,true);
    private Plan PreviewMappings(IReadOnlyList<RemapChoice> choices,bool allowAir,bool allowValid,bool createBackup,bool manualBackupAcknowledged,bool batch)
    {
        Guard();lock(gate)
        {
            if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Cancel the staged repair first.");
            if(scan?.State!="complete"||scan.Errors!=0||scan.Unsupported!=0)throw new InvalidOperationException("A complete, error-free saved-map scan is required. Legacy chunks must be converted first.");
            var resolved=RemapSelection.Resolve(choices,ResolveTarget);var map=Mapping();var mappings=new List<RepairMapping>();
            foreach(var pair in resolved)
            {
                var match=scan.Findings.SingleOrDefault(x=>x.Id==pair.Key);
                if(match==null||match.Count==0)throw new InvalidOperationException("A selected ID has no scanned occurrences. For registered sources use Advanced Diagnostic Scan.");
                var target=Missing(pair.Value)?null:api.World.Blocks[pair.Value];
                if(target!=null&&pair.Value!=0&&((match.Fluid>0&&!target.ForFluidsLayer)||((match.Solid>0||match.Decor>0)&&target.ForFluidsLayer)))throw new ArgumentException("Destination does not match the affected solid/fluid layer for source "+pair.Key+".");
                mappings.Add(new(){Source=pair.Key,Target=pair.Value,SourceCode=Label(pair.Key,map),TargetCode=target?.Code?.ToString()??"",Count=match.Count,Chunks=match.Chunks});
            }
            var first=mappings[0];var plan=new Plan{Mappings=batch?mappings:[],Source=first.Source,Target=first.Target,SourceCode=first.SourceCode,TargetCode=first.TargetCode,Count=mappings.Sum(m=>m.Count),Chunks=mappings.Sum(m=>m.Chunks),AllowAir=allowAir,AllowValidSource=allowValid,CreateBackup=createBackup,ManualBackupAcknowledged=manualBackupAcknowledged,World=api.World.SavegameIdentifier,Registry=scan.Registry,ScanId=scan.Id};Validate(plan);
            plan.Message="Preview only: "+mappings.Count+" mapping(s), "+plan.Count+" saved occurrences. Only checked source IDs are included; every saved occurrence of each source is replaced. Per-ID chunk counts overlap. "+(createBackup?"One verified automatic world backup will be created before edits.":"AUTOMATIC BACKUP DISABLED. Your acknowledged manual copy is the recovery path.")+" Stage to apply on restart.";preview=plan;return plan;
        }
    }
    public Plan StageBatch(string id,IReadOnlyList<RemapChoice> choices,bool allowAir,bool allowValid,bool createBackup,bool manualBackupAcknowledged)
    {
        lock(gate)
        {
            if(preview==null||preview.Id!=id||preview.Mappings.Count==0)throw new InvalidOperationException("Batch preview expired or missing.");
            var expected=RemapSelection.Resolve(choices,ResolveTarget);
            if(expected.Count!=preview.Mappings.Count||preview.Mappings.Any(m=>!expected.TryGetValue(m.Source,out int target)||target!=m.Target))throw new InvalidOperationException("Selected mappings changed; generate a fresh preview.");
            return Stage(id,expectedBackup:createBackup,expectedManual:manualBackupAcknowledged,expectedAir:allowAir,expectedValid:allowValid);
        }
    }
    public Plan Stage(string id,int? expectedSource=null,string? expectedTarget=null,bool? expectedBackup=null,bool? expectedManual=null,bool? expectedAir=null,bool? expectedValid=null)
    {
        Guard();lock(gate){if(restorePending!=null||selectedPending!=null)throw new InvalidOperationException("An inventory or selected repair is already staged.");if(preview==null||preview.Id!=id)throw new InvalidOperationException("Preview expired or missing.");if(preview.Mappings.Count>0&&expectedSource.HasValue)throw new InvalidOperationException("Use Stage Checked Remaps with the full reviewed mapping list.");if((expectedSource.HasValue&&expectedSource!=preview.Source)||(expectedTarget!=null&&ResolveTarget(expectedTarget)!=preview.Target)||(expectedBackup.HasValue&&expectedBackup!=preview.CreateBackup)||(expectedManual.HasValue&&expectedManual!=preview.ManualBackupAcknowledged)||(expectedAir.HasValue&&expectedAir!=preview.AllowAir)||(expectedValid.HasValue&&expectedValid!=preview.AllowValidSource))throw new InvalidOperationException("Inputs changed; generate a fresh preview.");Validate(preview);preview.State="pending";Write(pendingFile,preview);pending=preview;preview=null;return pending;}
    }
    public object Cancel()
    {
        lock(gate){cancel?.Cancel();selectedPreview=null;if(File.Exists(SelectedPendingFile))File.Delete(SelectedPendingFile);selectedPending=null;restorePreview=null;if(File.Exists(RestorePendingFile))File.Delete(RestorePendingFile);restorePending=null;preview=null;if(File.Exists(pendingFile))File.Delete(pendingFile);pending=null;return new{success=true,message="Scan cancellation requested; staged repair removed. No terrain changed."};}
    }
    public void ApplyBeforeTerrainLoads()
    {
        if(selectedPending!=null){ApplySelectedBeforeTerrainLoads();return;}
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
            pool=new ChunkDataPool(32,server){CacheSize=32};var codec=new BlockIdChunkCodec(pool);var mappings=EffectiveMappings(p);var replacements=mappings.ToDictionary(m=>m.Source,m=>m.Target);var totals=mappings.ToDictionary(m=>m.Source,_=>0L);long? cursor=null;long reviewed=0;
            // Full preflight avoids partial mutation when a source hosts block entities,
            // a legacy/corrupt chunk exists, or a layer mismatch was introduced later.
            while(true){var rows=Page(db.DatabaseFilename,cursor);if(rows.Count==0)break;foreach(var row in rows){cursor=row.Key;if(++reviewed%1024==0)api.Logger.Notification("[BlockRepair] Preflight: {0} saved chunks checked.",reviewed);var counts=codec.Inspect(row.Data,replacements.ContainsKey);foreach(var entry in counts){totals[entry.Key]+=entry.Value.Sum();var target=api.World.Blocks[replacements[entry.Key]];var c=entry.Value;if(target.BlockId!=0&&((c[1]>0&&!target.ForFluidsLayer)||((c[0]+c[2]>0)&&target.ForFluidsLayer)))throw new InvalidOperationException("Affected block layer changed since preview.");}if(counts.Count>0)codec.ReplaceMany(row.Data,replacements,out _);}}
            if(p.Mappings.Count>0&&mappings.Any(m=>totals[m.Source]!=m.Count))throw new InvalidOperationException("Selected source counts changed after preview. No terrain changed; rescan and review the mappings again.");
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
            while(true){var rows=Page(db.DatabaseFilename,cursor);if(rows.Count==0)break;var changes=new List<DbChunk>();long batchBlocks=0;foreach(var row in rows){cursor=row.Key;byte[] bytes=codec.ReplaceMany(row.Data,replacements,out long changed);if(changed==0)continue;changes.Add(new DbChunk(ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key)),bytes));batchBlocks+=changed;}
                if(changes.Count>0){if(api.Server.CurrentRunPhase!=EnumServerRunPhase.AssetsFinalize||((ChunkServerThread)thread).runOffThreadSaveNow||api.WorldManager.AllLoadedChunks.Count>0||Queued()>0)throw new InvalidOperationException("Another mod started terrain or saving during repair.");writes=true;db.SetChunks(changes);p.ChangedChunks+=changes.Count;p.ChangedBlocks+=batchBlocks;Write(historyFile,p);}
            }
            p.State="complete";p.Message="Saved-map repair complete. "+(p.CreateBackup?"Automatic backup retained.":"No automatic backup was made; retain your manual copy.")+" Cached lighting/heightmaps and external map exports may need refreshing.";
            api.Logger.Notification("[BlockRepair] Completed plan {0}, {1} mapping(s): {2} entries in {3} chunks. Backup: {4}",p.Id,mappings.Count,p.ChangedBlocks,p.ChangedChunks,p.Backup);
        }catch(Exception e){p.State=writes?"failed-partial":"failed";p.Message=e.Message+(writes?(p.CreateBackup?" Some chunks changed. Restore the retained world backup before retrying.":" Some chunks changed. No automatic backup exists. Restore your manual world copy before retrying."):" No terrain edits were made.");api.Logger.Error("[BlockRepair] {0}",p.Message);if(writes)((ServerMain)api.World).Stop("Block repair partially failed; restore backup before continuing",EnumExitMode.SoftExit);if(File.Exists(pendingFile))File.Move(pendingFile,Path.Combine(directory,"rejected-"+p.Id+".json"),true);pending=null;}
        finally{pool?.FreeAll();last=p;Write(historyFile,p);}
    }
    private void RegisterCommands()
    {
        var parsers=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseblockrepair").RequiresPrivilege(Privilege.controlserver).WithDescription("Saved-map numeric block ID inspection and staged repair")
            .BeginSubCommand("help").HandleWith(_=>TextCommandResult.Success(BlockLogger.Commands.RollbackChatText.Encode("Block repair: select (look at block), select above (missing plant above ground), or select at X Y Z [dimension]; results; suggest; replace domain:code confirm-risk [allow-air-source]; selectedstage ID. Containers use retained restoration and restorestage ID. Everything previews first and applies on restart. Global remaps use scan ID, preview ID destination, stage ID; these affect every occurrence. Backups default on. Experimental: keep a world copy."))).EndSubCommand()
            .BeginSubCommand("select").RequiresPlayer().WithArgs(parsers.OptionalAll("absolute coordinates or above")).HandleWith(a=>PlayerResult(a,p=>SelectInGame(p,a[0] as string??""))).EndSubCommand()
            .BeginSubCommand("results").RequiresPlayer().HandleWith(a=>PlayerResult(a,SelectedResults)).EndSubCommand()
            .BeginSubCommand("suggest").RequiresPlayer().HandleWith(a=>PlayerResult(a,p=>{var f=Focus(p);StartRecommendations(f.X,f.Y,f.Z,f.Dimension);return "Looking up exact-location BlockLogger history; use /nseblockrepair results to see suggestions and confidence.";})).EndSubCommand()
            .BeginSubCommand("replace").RequiresPlayer().WithArgs(parsers.Word("destination ID or code"),parsers.OptionalAll("options")).HandleWith(a=>PlayerResult(a,p=>ReplaceSelected(p,(string)a[0],a[1] as string??""))).EndSubCommand()
            .BeginSubCommand("selectedstage").WithArgs(parsers.Word("selected preview ID")).HandleWith(a=>Run(()=>StageSelected((string)a[0]))).EndSubCommand()
            .BeginSubCommand("status").HandleWith(_=>Run(()=>Snapshot())).EndSubCommand()
            .BeginSubCommand("scan").WithArgs(parsers.OptionalInt("source ID")).HandleWith(a=>Run(()=>StartScan(a.Parsers[0].IsMissing?null:(int)a[0]))).EndSubCommand()
            .BeginSubCommand("diagnostics").WithArgs(parsers.OptionalInt("source ID")).HandleWith(a=>Run(()=>StartScan(a.Parsers[0].IsMissing?null:(int)a[0],true))).EndSubCommand()
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
    private TextCommandResult Run(Func<object> fn){try{var result=fn();api.Logger.Notification("{0}","NSE_BLOCKREPAIR_RESULT "+JsonSerializer.Serialize(result));string message=result is SelectedPlan sp?$"Selected repair {sp.Id}: {sp.State}. {sp.Message}":result is InventoryRestorePlan rp?$"Inventory restore {rp.Id}: {rp.State}; {rp.Items} items at one location.":result is Plan p?$"Plan {p.Id}: {(p.Mappings.Count>0?p.Mappings.Count+" mapping(s)":"ID "+p.Source+" -> "+p.Target)}; {p.Count} scanned entries; {p.State}.":$"Block repair: scan {scan?.State??"none"}, chunks {scan?.Chunks??0}, errors {scan?.Errors??0}. Inspection {inspection?.State??"none"}. Selected repair {selectedPreview?.Id??selectedPending?.Id??"none"}: {selectedPreview?.State??selectedPending?.State??"none"}. Inventory restore {restorePreview?.Id??restorePending?.Id??"none"}: {restorePreview?.State??restorePending?.State??"none"}. Full result printed to console and available in the webpanel.";return TextCommandResult.Success(BlockLogger.Commands.RollbackChatText.Encode(message));}catch(Exception e){return TextCommandResult.Error(BlockLogger.Commands.RollbackChatText.Encode(e.Message.Replace('{','(').Replace('}',')')));}}
    public void Dispose(){lock(gate){disposed=true;cancel?.Cancel();}try{worker?.Wait(3000);}catch{}if(worker?.IsCompleted!=false)cancel?.Dispose();}
}
