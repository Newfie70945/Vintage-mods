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
        public string AutomaticIssue {get;set;}="";public string Category {get;set;}="unresolved-id";public string CurrentCode {get;set;}="";public int? SameCodeRegisteredId {get;set;}
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
        public MigrationInventoryOptions Inventory {get;set;}=new();public MigrationInventoryReport InventoryReport {get;set;}=new();public string InventoryRegistry {get;set;}="";public string InventoryManifestHash {get;set;}="";
        public bool LoadedOnly {get;set;}public bool Automatic {get;set;}public List<AutomaticSkip> Skipped {get;set;}=[];
        public List<RepairMapping> Mappings {get;set;}=[];
        public int Source {get;set;} public int Target {get;set;} public string SourceCode {get;set;}="";public string TargetCode {get;set;}="";
        public long Count {get;set;}public long Chunks {get;set;}public bool AllowAir {get;set;}public bool AllowValidSource {get;set;}public string State {get;set;}="preview";
        public bool CreateBackup {get;set;}=true;public bool ManualBackupAcknowledged {get;set;}
        public string Backup {get;set;}="";public string Message {get;set;}="";public long ChangedBlocks {get;set;}public long ChangedChunks {get;set;}
    }
    private readonly ICoreServerAPI api;private readonly object gate=new();private readonly string directory;private readonly string pendingFile;private readonly string historyFile;
    private Scan? scan;private Plan? preview;private Plan? pending;private Plan? last;private CancellationTokenSource? cancel;private Task? worker;private bool disposed;
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public BlockIdRepairService(ICoreServerAPI api){this.api=api;directory=api.GetOrCreateDataPath("NewfiesBlockRepair");pendingFile=Path.Combine(directory,"pending.json");historyFile=Path.Combine(directory,"last-repair.json");try{pending=Read<Plan>(pendingFile);last=Read<Plan>(historyFile);}catch(Exception e){last=new Plan{State="failed",Message="Repair state file could not be read: "+e.Message};api.Logger.Error("[BlockRepair] {0}",last.Message);}LoadInventoryRestore();LoadSelectedRepair();RetireLegacyJobs();LoadLoadedMigration();RegisterCommands();api.Event.ServerRunPhase(EnumServerRunPhase.RunGame,ResumeLiveMigration);}
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
        lock(gate)return new{world=api.World.SavegameIdentifier,scan,preview=includeInspection?preview:null,loadedMigration=LoadedMigrationSnapshot(includeInspection),mappingAdvice=includeInspection?mappingAdvice:null,
            scope="Only missing definitions, code-less remapper holes and undefined saved numeric IDs. Healthy registered source IDs are protected. Migration processes safe loaded cells and saved chunks as they load; chiseled material rules preserve the host/shape. It does not load terrain itself.",
            limitations="Only configured terrain/fluid/decor and numeric chiseled material IDs are migrated. Compatible retained block-entity bytes are preserved for exact-code reconnection. Deleted inventories, inventory stack IDs, client texture errors and plants saved as air are not reconstructed."};
    }
    public object StartScan(int? focus,bool diagnostics=false)
    {
        Guard();lock(gate){if(worker is{IsCompleted:false})throw new InvalidOperationException("A scan is already running.");if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Cancel the staged repair before scanning again.");
            if(focus==0)throw new ArgumentException("Air ID 0 cannot be a source.");var map=Mapping();var placeholders=api.World.Blocks.Select(MissingBlockScan.IsPlaceholder).ToArray();if(!diagnostics&&focus.HasValue&&(!Missing(focus.Value)))throw new ArgumentException("This source is registered and protected. Use Advanced Diagnostic Scan for read-only investigation.");var unresolved=api.World.Blocks.Select(b=>b==null||b.Code==null||b.IsMissing).ToArray();var missing=api.World.Blocks.Select((b,i)=>unresolved[i]||RetainedBlockFindings.MappingMismatch(i,map.GetValueOrDefault(i),b?.Code?.ToString())).ToArray();var available=new Dictionary<int,int>();foreach(var entry in map.Where(e=>e.Key!=0&&(e.Key<0||e.Key>=missing.Length||missing[e.Key]))){var block=api.World.GetBlock(new AssetLocation(entry.Value));if(block?.Code!=null&&!block.IsMissing)available[entry.Key]=block.BlockId;}var identities=available.Where(e=>Missing(e.Key)&&e.Value!=e.Key).ToDictionary(e=>e.Key,e=>{var b=api.World.Blocks[e.Value];return new BlockIdChunkCodec.Identity(b.Code.ToString(),b.EntityClass??"",b.ForFluidsLayer);});var codes=api.World.Blocks.Select(b=>b?.Code?.ToString()??"").ToArray();var classes=api.World.Blocks.Select(b=>b?.EntityClass??"").ToArray();string registry=Fingerprint(map),path=Database().DatabaseFilename;
            cancel?.Dispose();cancel=new();var token=cancel.Token;mappingAdvice=null;scan=new(){FocusId=focus,Mode=diagnostics?"diagnostics":"missing-blocks",Registry=registry};preview=null;var current=scan;var server=(ServerMain)api.World;
            worker=Task.Run(()=>ScanSaved(current,path,map,missing,unresolved,placeholders,available,identities,codes,classes,server,token));return new{success=true,scanId=current.Id};}
    }
    private static SqliteConnection Open(string path){var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString());connection.Open();return connection;}
    private static List<(long Key,byte[] Data)> Page(string path,long? after)
    {
        using var connection=Open(path);using var cmd=connection.CreateCommand();cmd.CommandTimeout=2;cmd.CommandText=after.HasValue?"SELECT position,data FROM chunk WHERE position>$after ORDER BY position LIMIT 16":"SELECT position,data FROM chunk ORDER BY position LIMIT 16";if(after.HasValue)cmd.Parameters.AddWithValue("$after",after.Value);
        using var reader=cmd.ExecuteReader();var rows=new List<(long,byte[])>();long pageBytes=0;while(reader.Read()){long length=reader.GetBytes(1,0,null,0,0);if(length>64*1024*1024)throw new InvalidDataException("Oversized saved chunk.");if(rows.Count>0&&pageBytes+length>8*1024*1024)break;pageBytes+=length;rows.Add((reader.GetInt64(0),(byte[])reader[1]));}return rows;
    }
    private void ScanSaved(Scan current,string path,Dictionary<int,string> map,bool[] missing,bool[] unresolved,bool[] placeholders,Dictionary<int,int> available,Dictionary<int,BlockIdChunkCodec.Identity> identities,string[] codes,string[] classes,ServerMain server,CancellationToken token)
    {
        var pool=new ChunkDataPool(32,server){CacheSize=32};var codec=new BlockIdChunkCodec(pool);var found=new Dictionary<int,Finding>();var samples=new Dictionary<int,List<SamplePosition>>();long? cursor=null;long scanned=0,errors=0,unsupported=0;
        try{while(true){token.ThrowIfCancellationRequested();var rows=Page(path,cursor);if(rows.Count==0)break;foreach(var row in rows){token.ThrowIfCancellationRequested();cursor=row.Key;scanned++;
                try{var sampleChunk=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));if(current.Mode=="diagnostics"){var orphaned=RetainedBlockFindings.Inspect(row.Data,codec,id=>id>=0&&id<codes.Length?codes[id]:"",id=>id>=0&&id<classes.Length?classes[id]:"",out int unverified);var chunk=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));orphaned=orphaned.Where(e=>e.X/32==chunk.X&&e.Y/32==chunk.Y&&e.Z/32==chunk.Z&&e.Dimension==chunk.Dimension&&(!current.FocusId.HasValue||e.SolidId==current.FocusId.Value)).ToList();lock(gate){current.UnverifiedContainers+=unverified;current.RetainedContainerCount+=orphaned.Count;current.RetainedContainers.AddRange(orphaned.Take(Math.Max(0,256-current.RetainedContainers.Count)));}}
                    var counts=codec.Inspect(row.Data,id=>MissingBlockScan.Includes(id,current.FocusId,current.Mode=="diagnostics",placeholders,missing),(id,layer,index)=>{if(layer!=0)return;if(!samples.TryGetValue(id,out var positions)){if(samples.Count>=4096)return;samples[id]=positions=[];}if(positions.Count<2&&!positions.Any(p=>p.X/32==sampleChunk.X&&p.Y/32==sampleChunk.Y&&p.Z/32==sampleChunk.Z&&p.Dimension==sampleChunk.Dimension))positions.Add(new(sampleChunk.X*32+(index&31),sampleChunk.Y*32+(index>>10),sampleChunk.Z*32+((index>>5)&31),sampleChunk.Dimension));});var identityIssues=codec.CheckIdentityRecovery(row.Data,counts,identities,sampleChunk);foreach(var item in counts){if(!found.TryGetValue(item.Key,out var finding)){if(found.Count>=4096)throw new InvalidDataException("Too many distinct unresolved IDs.");found[item.Key]=finding=new(){Id=item.Key,Code=map.GetValueOrDefault(item.Key,""),CurrentCode=item.Key>=0&&item.Key<codes.Length?codes[item.Key]:"",SameCodeRegisteredId=available.TryGetValue(item.Key,out int registeredId)?registeredId:null,Category=item.Key>=0&&item.Key<placeholders.Length&&placeholders[item.Key]?"missing-placeholder":item.Key>=0&&item.Key<unresolved.Length&&!unresolved[item.Key]?(missing[item.Key]?"registered-mismatch":"selected-registered"):"unresolved-id",Problem=item.Key>=0&&item.Key<placeholders.Length&&placeholders[item.Key]?"Missing-block (?) placeholder":item.Key>=0&&item.Key<unresolved.Length&&!unresolved[item.Key]?(missing[item.Key]?"Saved/current code mismatch; current block is registered (review required)":"Selected registered ID; not a missing-block finding"):"Unresolved saved ID; visible damage not verified"};}
                        if(identityIssues.TryGetValue(item.Key,out string? identityIssue))finding.AutomaticIssue=identityIssue;finding.Positions=samples.GetValueOrDefault(item.Key,[]);finding.Solid+=item.Value[0];finding.Fluid+=item.Value[1];finding.Decor+=item.Value[2];finding.Chunks++;if(finding.Examples.Count<5){var pos=ChunkPos.FromChunkIndex_saveGamev2(unchecked((ulong)row.Key));finding.Examples.Add($"chunk {pos.X}, {pos.Y}, {pos.Z}; key {row.Key}");}}
                }catch(NotSupportedException){unsupported++;}catch{errors++;}Thread.Sleep(2);}
                lock(gate){current.Chunks=scanned;current.Errors=errors;current.Unsupported=unsupported;current.Findings=found.Values.OrderByDescending(x=>x.Count).Select(Clone).ToList();}
            }
            lock(gate){current.State="complete";current.Message=(current.Mode=="diagnostics"?"Advanced diagnostic scan finished. These findings are not all question-mark blocks. Review retained block entities separately.":"Missing-block (?) scan finished. Missing definitions and undefined numeric slots were counted; healthy registered blocks were excluded. Compatible retained metadata was checked for automatic recovery.")+" Counts include underground/unloaded saved occurrences. Per-ID chunk counts overlap. Plants already saved as air require exact-location history/backup evidence; air is not globally scanned as damage. Concurrent saves can change counts.";}
        }catch(OperationCanceledException){lock(gate){current.State="cancelled";current.Message="Scan cancelled; no terrain was changed.";}}
        catch(Exception e){lock(gate){current.State="failed";current.Message="Scan failed: "+e.Message;}}
        finally{lock(gate){current.Chunks=scanned;current.Errors=errors;current.Unsupported=unsupported;current.Findings=found.Values.OrderByDescending(x=>x.Count).Select(Clone).ToList();current.FinishedUtc=DateTime.UtcNow;}pool.FreeAll();}
    }
    private static Finding Clone(Finding f)=>new(){Id=f.Id,Code=f.Code,Problem=f.Problem,Solid=f.Solid,Fluid=f.Fluid,Decor=f.Decor,Chunks=f.Chunks,Examples=f.Examples.ToList(),AutomaticIssue=f.AutomaticIssue,Category=f.Category,CurrentCode=f.CurrentCode,SameCodeRegisteredId=f.SameCodeRegisteredId,Positions=f.Positions.ToList()};
    private static List<RepairMapping> EffectiveMappings(Plan p)=>p.Mappings.Count>0?p.Mappings:[new(){Source=p.Source,Target=p.Target,SourceCode=p.SourceCode,TargetCode=p.TargetCode,Count=p.Count,Chunks=p.Chunks}];
    private void Validate(Plan p,bool inventoryReady=true)
    {
        if(!SavedBlockEntityInspector.BuiltinTypesSupported())throw new NotSupportedException("A mod changed built-in saved attribute types; repair is disabled to protect retained block entities.");
        if(!p.CreateBackup&&!p.ManualBackupAcknowledged)throw new InvalidOperationException("Automatic backup is disabled. Make a manual copy of your world and acknowledge it before previewing or staging a repair.");
        ValidateInventory(p,inventoryReady);
        var currentMap=Mapping();
        if(p.World!=api.World.SavegameIdentifier||p.Registry!=Fingerprint(currentMap))throw new InvalidOperationException("World or block-ID mapping changed; make a fresh scan and preview.");
        var mappings=EffectiveMappings(p);RemapSelection.Resolve(mappings.Select(m=>new RemapChoice(m.Source,m.Target.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(),int.Parse,p.Automatic||p.LoadedOnly?4096:RemapSelection.Limit);
        foreach(var m in mappings)
        {
            if(!Missing(m.Source))throw new InvalidOperationException("Source is registered and protected. Global recovery only changes missing definitions.");
            if(m.Target==0&&!p.AllowAir)throw new InvalidOperationException("Replacing with air requires the explicit allow-air option.");
            if(Missing(m.Target))throw new ArgumentException("Destination is missing or unregistered.");var target=api.World.Blocks[m.Target];
            if(m.TargetCode!=target.Code.ToString())throw new InvalidOperationException("Destination code no longer matches its reviewed numeric ID; create a fresh preview.");
            if(p.Automatic&&(m.Target==0||BlockIdChunkCodec.Canonical(currentMap.GetValueOrDefault(m.Source,""))!=m.TargetCode))throw new InvalidOperationException("Automatic migration requires the exact original saved code; replacements are never guessed.");
            if(!p.Automatic&&!string.IsNullOrEmpty(target.EntityClass)&&BlockIdChunkCodec.Canonical(currentMap.GetValueOrDefault(m.Source,""))!=m.TargetCode&&(p.Inventory.Mode=="preserve"||!SupportedContainer(target.EntityClass)||!m.TargetCode.StartsWith("game:",StringComparison.Ordinal)))throw new ArgumentException("Destination requires block-entity data. Only exact original-code migration can reconnect compatible retained metadata.");
            if(!p.LoadedOnly&&p.Mappings.Count>0&&m.Count<=0)throw new ArgumentException("Every selected mapping must have scanned occurrences.");
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
    public Plan PreviewBatch(IReadOnlyList<RemapChoice> choices,bool allowAir,bool allowValid,bool createBackup=true,bool manualBackupAcknowledged=false,MigrationInventoryOptions? inventory=null)
        =>PreviewMappings(choices,allowAir,allowValid,createBackup,manualBackupAcknowledged,true,false,inventory);
    private Plan PreviewMappings(IReadOnlyList<RemapChoice> choices,bool allowAir,bool allowValid,bool createBackup,bool manualBackupAcknowledged,bool batch,bool automatic=false,MigrationInventoryOptions? inventory=null)
    {
        Guard();lock(gate)
        {
            if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for the current scan/inventory preparation to finish or cancel it first.");
            if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Cancel the staged repair first.");
            if(scan?.State!="complete"||scan.Errors!=0||scan.Unsupported!=0)throw new InvalidOperationException("A complete, error-free missing-block scan is required. Legacy chunks must be converted first.");
            inventory??=new();inventory.Validate();
            var resolved=RemapSelection.Resolve(choices,ResolveTarget,automatic?4096:RemapSelection.Limit);var map=Mapping();var mappings=new List<RepairMapping>();
            foreach(var pair in resolved)mappings.Add(ResolveMapping(pair.Key,pair.Value,scan,map,allowAir,allowValid,automatic,inventory));
            var first=mappings[0];var plan=new Plan{Inventory=inventory,Automatic=automatic,Mappings=batch?mappings:[],Source=first.Source,Target=first.Target,SourceCode=first.SourceCode,TargetCode=first.TargetCode,Count=mappings.Sum(m=>m.Count),Chunks=mappings.Sum(m=>m.Chunks),AllowAir=allowAir,AllowValidSource=allowValid,CreateBackup=createBackup,ManualBackupAcknowledged=manualBackupAcknowledged,World=api.World.SavegameIdentifier,Registry=scan.Registry,ScanId=scan.Id};if(inventory.Mode!="preserve")plan.InventoryRegistry=RestoreRegistry();Validate(plan,false);
            plan.Message="Preview only: "+mappings.Count+" mapping(s), "+plan.Count+" saved occurrences. Only checked source IDs are included; every saved occurrence of each source is replaced. Per-ID chunk counts overlap. "+(createBackup?"One verified automatic world backup will be created before edits.":"AUTOMATIC BACKUP DISABLED. Your acknowledged manual copy is the recovery path.")+" Enable migration to apply these IDs as saved chunks load after restart.";preview=plan;StartMigrationInventoryPreparation(plan);return plan;
        }
    }
    public object Cancel()
    {
        lock(gate){if(loadedMigration.Enabled&&!loadedActive)DisableLoadedMigration();cancel?.Cancel();selectedPreview=null;if(File.Exists(SelectedPendingFile))File.Delete(SelectedPendingFile);selectedPending=null;restorePreview=null;if(File.Exists(RestorePendingFile))File.Delete(RestorePendingFile);restorePending=null;preview=null;if(File.Exists(pendingFile))File.Delete(pendingFile);pending=null;return new{success=true,message="Scan cancellation requested; staged repair removed. No terrain changed."};}
    }
    public void ApplyBeforeTerrainLoads()=>ActivateLoadedMigration();
    private void RegisterCommands()
    {
        var p=api.ChatCommands.Parsers;
        api.ChatCommands.Create("nseblockrepair").RequiresPrivilege(Privilege.controlserver).HandleWith(_=>TextCommandResult.Success("Block ID Repair was replaced by Block ID Migration. Use /nseblockmigrate help or the webpanel Block ID Migration page."));
        api.ChatCommands.Create("nseblockmigrate").RequiresPrivilege(Privilege.controlserver).WithDescription("Configure missing block ID migrations as saved chunks load")
            .BeginSubCommand("help").HandleWith(_=>TextCommandResult.Success(BlockLogger.Commands.RollbackChatText.Encode("Block ID Migration (experimental): inspect shows identity and matching rules; previewheld checks your held replacement without saving; repairheld confirm-risk manual-copy-confirmed repairs ONE plain missing block (no automatic backup; containers/chiseled metadata refused). scan [ID]; preview OLDID domain:code [allow-air] [no-backup manual-copy-confirmed]; automigrate (all exact installed codes); enable PREVIEWID (automatic backup and live activation); disable; status; rules [page]; cancel. replaceheld stages an ID-wide rule from the missing block you look at to your held block, preserving other configured rules; enables automatically after backup. Skip automatic backup: replaceheld no-backup manual-copy-confirmed (make a manual copy first). Safe already-loaded solid blocks are processed after activation. Chiseled blocks: inspect; replaceheld source=ID chooses a missing material without replacing the chiseled shape; at=X,Y,Z dimension=N targets an unselectable loaded cell. Bookshelves: replaceheld reconnect confirm-inventory-risk preserves compatible retained contents without history. Inventory option: replaceheld recover-empty confirm-inventory-risk [before=ISO-UTC]; replace also needs confirm-overwrite. Rules save without waiting for scans. Optional inventory preparation and backup finish before automatic activation; incompatible containers are skipped. Only missing source IDs change. Chunks migrate when loaded and persist on normal saves. Make a world backup. Deleted inventory data cannot be invented. The webpanel supports multiple per-ID rules and suggestions."))).EndSubCommand()
            .BeginSubCommand("inspect").RequiresPlayer().WithArgs(p.OptionalAll("at=X,Y,Z dimension=N")).HandleWith(a=>PlayerResult(a,player=>InspectMigrationTarget(player,a[0] as string??""))).EndSubCommand()
            .BeginSubCommand("previewheld").RequiresPlayer().WithArgs(p.OptionalAll("replacement options")).HandleWith(a=>PlayerResult(a,player=>ReviewHeldReplacement(player,a[0] as string??"",true,false))).EndSubCommand()
            .BeginSubCommand("repairheld").RequiresPlayer().WithArgs(p.OptionalAll("confirm-risk manual-copy-confirmed")).HandleWith(a=>PlayerResult(a,player=>ReviewHeldReplacement(player,a[0] as string??"",false,true))).EndSubCommand()
            .BeginSubCommand("replaceheld").RequiresPlayer().WithArgs(p.OptionalAll("inventory options")).HandleWith(a=>PlayerResult(a,player=>ReplaceLookedAtHeldOptions(player,a[0] as string??""))).EndSubCommand()
            .BeginSubCommand("scan").WithArgs(p.OptionalInt("missing source ID")).HandleWith(a=>Run(()=>StartScan(a.Parsers[0].IsMissing?null:(int)a[0]))).EndSubCommand()
            .BeginSubCommand("preview").WithArgs(p.Int("missing source ID"),p.Word("destination ID or code"),p.OptionalAll("options")).HandleWith(a=>Run(()=>MigrationCommandPreview((int)a[0],(string)a[1],a[2] as string??""))).EndSubCommand()
            .BeginSubCommand("automigrate").HandleWith(_=>Run(()=>PreviewAutomatic())).EndSubCommand()
            .BeginSubCommand("enable").WithArgs(p.Word("reviewed preview ID")).HandleWith(a=>Run(()=>StageLoadedMigration((string)a[0],preview?.CreateBackup??true,preview?.ManualBackupAcknowledged??false))).EndSubCommand()
            .BeginSubCommand("disable").HandleWith(_=>Run(DisableLoadedMigration)).EndSubCommand()
            .BeginSubCommand("status").HandleWith(_=>Run(()=>Snapshot())).EndSubCommand()
            .BeginSubCommand("rules").WithArgs(p.OptionalInt("page")).HandleWith(a=>ListMigrationRules(a.Parsers[0].IsMissing?1:(int)a[0])).EndSubCommand()
            .BeginSubCommand("cancel").HandleWith(_=>Run(Cancel)).EndSubCommand();
    }
    private Plan MigrationCommandPreview(int source,string destination,string options)
    {
        var flags=options.Split(' ',StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if(flags.Any(f=>f is not ("allow-air" or "no-backup" or "manual-copy-confirmed")))throw new ArgumentException("Options: allow-air, no-backup manual-copy-confirmed. Healthy source IDs cannot be overridden.");
        return PreviewBatch([new(source,destination)],flags.Contains("allow-air"),false,!flags.Contains("no-backup"),flags.Contains("manual-copy-confirmed"));
    }
    private TextCommandResult Run(Func<object> fn)
    {
        try{var result=fn();api.Logger.Notification("{0}","NSE_BLOCKMIGRATION_RESULT "+JsonSerializer.Serialize(result));string message=result is Plan plan?$"Migration preview {plan.Id}: {EffectiveMappings(plan).Count} mapping(s), {plan.Count} saved occurrences. Use /nseblockmigrate enable {plan.Id} to back up and enable migration automatically.":$"Migration {loadedMigration.State}: {loadedMigration.Message} Changed {loadedBlocks+liveSweepBlocks} blocks and {migratedMaterialReferences} chiseled material references; loaded terrain pending {liveChunkKeys.Count+(liveChunkKey.HasValue?1:0)} chunks, skipped {liveSweepSkipped} cells. {loadedLastIssue} {liveSweepIssue} {loadedInventoryLastIssue} Details in the webpanel.";return TextCommandResult.Success(BlockLogger.Commands.RollbackChatText.Encode(message));}
        catch(Exception e){return TextCommandResult.Error(BlockLogger.Commands.RollbackChatText.Encode(e.Message));}
    }
    private void RetireLegacyJobs()
    {
        foreach(string file in new[]{pendingFile,RestorePendingFile,SelectedPendingFile})if(File.Exists(file))File.Move(file,file+".retired",true);
        if(pending!=null||restorePending!=null||selectedPending!=null)api.Logger.Warning("[BlockMigration] Old block-repair jobs retired without world edits. Review new migration rules in the webpanel.");
        pending=null;restorePending=null;selectedPending=null;
    }
    public void Dispose(){lock(gate){disposed=true;cancel?.Cancel();CancelLiveActivation();}DisposeLoadedMigration();try{worker?.Wait(3000);liveActivationWorker?.Wait(3000);}catch{}if(worker?.IsCompleted!=false)cancel?.Dispose();}
}
