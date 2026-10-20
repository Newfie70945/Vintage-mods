using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed class LoadedMigration
    {
        public bool Enabled {get;set;}public string State {get;set;}="disabled";public string Message {get;set;}="";
        public Plan? Plan {get;set;}
    }
    private string LoadedMigrationFile=>Path.Combine(directory,"loaded-chunk-migration.json");
    private readonly object loadedGate=new();private LoadedMigration loadedMigration=new();private Harmony? loadedHarmony;
    private static BlockIdRepairService? loadedOwner;
    private ChunkDataPool? loadedPool;private BlockIdChunkCodec? loadedCodec;
    private Dictionary<int,int> loadedReplacements=[];private Dictionary<int,BlockIdChunkCodec.Identity> loadedIdentities=[];
    private Dictionary<int,bool> loadedFluids=[];private bool loadedActive;
    private long loadedChecked,loadedChanged,loadedBlocks,loadedRefused;private string loadedLastIssue="";
    private void LoadLoadedMigration()
    {
        try{loadedMigration=Read<LoadedMigration>(LoadedMigrationFile)??new();}
        catch(Exception e){loadedMigration=new(){State="failed",Message="Cannot read migration settings: "+e.Message};}
    }
    private bool LoadedConfigured()=>loadedMigration.Enabled;
    private object LoadedMigrationSnapshot(bool includeInventoryDetails=true)
    {
        lock(loadedGate)return new{loadedMigration.Enabled,loadedMigration.State,loadedMigration.Message,Plan=includeInventoryDetails?(object?)loadedMigration.Plan:loadedMigration.Plan==null?null:new{loadedMigration.Plan.Id,loadedMigration.Plan.Mappings,loadedMigration.Plan.Source,loadedMigration.Plan.Target,loadedMigration.Plan.SourceCode,loadedMigration.Plan.TargetCode,loadedMigration.Plan.Inventory},active=loadedActive,sessionChunksChecked=loadedChecked,sessionChunksChanged=loadedChanged,sessionBlocksChanged=loadedBlocks,sessionChunksRefused=loadedRefused,sessionInventoriesRecovered=loadedInventoriesRecovered,sessionInventoryItemsRecovered=loadedInventoryItems,sessionInventoriesPreserved=loadedInventoriesPreserved,sessionInventoryRecoveriesSkipped=loadedInventorySkipped,lastInventoryIssue=loadedInventoryLastIssue,loadedSweepPending=liveChunkKeys.Count+(liveChunkKey.HasValue?1:0),loadedSweepBlocksChanged=liveSweepBlocks,loadedSweepBlocksSkipped=liveSweepSkipped,lastLoadedSweepIssue=liveSweepIssue,lastIssue=loadedLastIssue};
    }
    public object StageLoadedMigration(string previewId,bool createBackup,bool manualBackupAcknowledged,MigrationInventoryOptions? inventory=null)
    {
        Guard();lock(gate)
        {
            if(worker is{IsCompleted:false}||pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Wait for scans and cancel other staged repairs first.");
            if(LoadedConfigured())throw new InvalidOperationException("Disable the current loaded-chunk migration before replacing its settings.");
            if(preview==null||preview.Id!=previewId||preview.CreateBackup!=createBackup||preview.ManualBackupAcknowledged!=manualBackupAcknowledged)throw new InvalidOperationException("Preview or backup options changed. Prepare a fresh preview.");
            if(inventory!=null&&inventory!=preview.Inventory)throw new InvalidOperationException("Inventory options changed; review and prepare a new migration first.");
            Validate(preview);
            SaveLoadedConfiguration(preview);preview=null;return LoadedMigrationSnapshot();
        }
    }
    private void SaveLoadedConfiguration(Plan plan)
    {
        lock(loadedGate)
        {
            var settings=new LoadedMigration{Enabled=true,State=plan.Inventory.Mode!="preserve"&&plan.InventoryReport.State!="complete"?"preparing-inventories":"activating",Plan=plan,Message="Rule saved. Backup/preparation runs automatically, then migration enables without a restart. Safe already-loaded solid blocks are processed in bounded batches after activation; inventory recovery and other layers require reloading. Normal saves persist changes."};
            CancelLiveActivation();StopLoadedTerrainSweep();Write(LoadedMigrationFile,settings);loadedActive=false;loadedMigration=settings;
        }
        if(plan.Inventory.Mode=="preserve"||plan.InventoryReport.State=="complete")QueueLiveActivation();
    }
    public object DisableLoadedMigration()
    {
        lock(gate)lock(loadedGate)
        {
            CancelLiveActivation();StopLoadedTerrainSweep();loadedActive=false;loadedMigration.Enabled=false;loadedMigration.State="disabled";
            loadedMigration.Message="Further chunk migrations disabled. Previously migrated blocks are not reverted.";
            Write(LoadedMigrationFile,loadedMigration);return LoadedMigrationSnapshot();
        }
    }
    private void ActivateLoadedMigration(bool startup=true)
    {
        if(!LoadedConfigured()||(startup&&loadedMigration.State!="active"))return;
        if(startup&&loadedMigration.Plan is{Inventory.Mode:not "preserve",InventoryReport.State:not "complete"})return;
        lock(loadedGate)
        {
        try
        {
            Guard();var p=loadedMigration.Plan??throw new InvalidOperationException("Migration has no reviewed mappings.");Validate(p);
            if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("A separate repair is staged. Disable loaded migration before other repairs.");
            if(startup?(api.Server.CurrentRunPhase!=EnumServerRunPhase.AssetsFinalize||api.WorldManager.AllLoadedChunks.Count>0):api.Server.CurrentRunPhase!=EnumServerRunPhase.RunGame)throw new InvalidOperationException("Migration activation requires startup or a running official server.");
            var method=AccessTools.Method(typeof(ServerChunk),nameof(ServerChunk.FromBytes),[typeof(byte[]),typeof(ChunkDataPool),typeof(IWorldAccessor)]);
            if(method==null||Harmony.GetPatchInfo(method)?.Owners.Any(owner=>owner!="newfies.suite.loaded-block-migration")==true)throw new NotSupportedException("Another mod patches chunk deserialization. Loaded migration disabled to protect saved data.");
            if(loadedOwner!=null&&loadedOwner!=this)throw new InvalidOperationException("Another loaded migration instance is active.");
            if(p.CreateBackup)
            {
                // Retain the first verified backup across restarts. Each new reviewed
                // configuration gets a fresh plan ID and therefore its own backup.
                string backup=Path.Combine(directory,"world-before-loaded-"+p.Id+".vcdbs");
                if(p.Backup!=backup||!File.Exists(backup))
                {
                    if(!startup)throw new InvalidOperationException("A verified background backup is required before live activation.");
                    Database().CreateBackup(backup);using var connection=Open(backup);using var check=connection.CreateCommand();check.CommandTimeout=600;check.CommandText="PRAGMA quick_check";
                    if(!string.Equals(Convert.ToString(check.ExecuteScalar()),"ok",StringComparison.Ordinal))throw new InvalidDataException("Migration backup integrity check failed.");
                    p.Backup=backup;
                }
            }
            var mappings=EffectiveMappings(p);loadedReplacements=mappings.ToDictionary(m=>m.Source,m=>m.Target);
            loadedFluids=mappings.ToDictionary(m=>m.Source,m=>api.World.Blocks[m.Target].ForFluidsLayer);
            loadedIdentities=mappings.Where(m=>m.Target!=0&&BlockIdChunkCodec.Canonical(m.SourceCode)==m.TargetCode).ToDictionary(m=>m.Source,m=>new BlockIdChunkCodec.Identity(m.TargetCode,api.World.Blocks[m.Target].EntityClass??"",api.World.Blocks[m.Target].ForFluidsLayer));
            loadedInventorySources.Clear();
            if(p.Inventory.Mode!="preserve")
            {
                var catalog=CaptureRestoreCatalog();foreach(var mapping in mappings)if(catalog.Blocks.TryGetValue(mapping.TargetCode,out var target)&&target.Supported)
                {loadedInventorySources.Add(mapping.Source);loadedIdentities[mapping.Source]=new(mapping.TargetCode,target.Class,false);}
                loadedManifest=InventoryManifest(p);loadedInventoryWorld=p.World;loadedInventoryPlan=p.Id;MigrationInventoryStore.EnsureReceipts(InventoryReceipts);
            }
            loadedPlainReplacements=loadedReplacements.Where(m=>!loadedInventorySources.Contains(m.Key)).ToDictionary(m=>m.Key,m=>m.Value);
            loadedPool??=new ChunkDataPool(32,(ServerMain)api.World){CacheSize=32};loadedCodec??=new(loadedPool);
            loadedHarmony??=new Harmony("newfies.suite.loaded-block-migration");loadedOwner=this;
            if(Harmony.GetPatchInfo(method)?.Owners.Contains("newfies.suite.loaded-block-migration")!=true)loadedHarmony.Patch(method,prefix:new HarmonyMethod(typeof(BlockIdRepairService),nameof(BeforeLoadedChunk)),postfix:new HarmonyMethod(typeof(BlockIdRepairService),nameof(AfterLoadedChunk)));
            loadedMigration.State="active";loadedMigration.Message="Configured missing IDs are migrated before each saved chunk is deserialized. Only successful loads are marked for normal saving. Safe already-loaded solid blocks are processed in bounded batches. Incompatible cells/chunks are skipped and reported; inventory recovery requires saved-chunk loading. No missing inventory data is invented.";
            Write(LoadedMigrationFile,loadedMigration);loadedActive=true;StartLoadedTerrainSweep();
            api.Logger.Notification("[BlockMigration] Loaded-chunk migration active: {0} mappings. Backup: {1}",mappings.Count,p.Backup);
        }
        catch(Exception e)
        {
            loadedActive=false;loadedMigration.State="failed";loadedMigration.Message=e.Message+" No new loaded-chunk migrations enabled.";
            Write(LoadedMigrationFile,loadedMigration);
            api.Logger.Error("[BlockMigration] {0}",loadedMigration.Message);
        }
        }
    }
    private sealed record LoadedChange(long Blocks,long Recovered,long Preserved,long Items);
    private static void BeforeLoadedChunk(ref byte[] __0,IWorldAccessor __2,out LoadedChange? __state)
    {
        __state=null;var owner=loadedOwner;if(owner==null||!ReferenceEquals(owner.api.World,__2))return;
        lock(owner.loadedGate)
        {
            if(!owner.loadedActive||owner.loadedCodec==null)return;
            try
            {
                owner.loadedChecked++;var counts=owner.loadedCodec.Inspect(__0,owner.loadedReplacements.ContainsKey);if(counts.Count==0)return;
                foreach(var entry in counts)
                {
                    if(!owner.Missing(entry.Key))throw new InvalidDataException("Source ID became registered; it is protected from migration.");
                    int target=owner.loadedReplacements[entry.Key];bool fluid=owner.loadedFluids[entry.Key];var c=entry.Value;
                    if(target!=0&&((c[1]>0&&!fluid)||((c[0]+c[2]>0)&&fluid)))throw new InvalidDataException("Replacement conflicts with the affected solid/fluid layer for ID "+entry.Key+".");
                }
                var recovery=owner.PrepareLoadedInventories(__0,counts);
                long plainChanged=0;var bytes=owner.loadedPlainReplacements.Count>0?owner.loadedCodec.ReplaceMany(recovery.Bytes,owner.loadedPlainReplacements,out plainChanged,owner.loadedIdentities):recovery.Bytes;
                long changed=plainChanged+recovery.ContainerBlocks;owner.loadedInventorySkipped+=recovery.Skipped;if(recovery.Issue.Length>0)owner.loadedInventoryLastIssue=recovery.Issue;
                if(changed>0)
                {
                    if(recovery.Entries.Any(e=>e.SnapshotHash.Length>0))MigrationInventoryStore.Reserve(owner.InventoryReceipts,owner.loadedInventoryWorld,owner.loadedInventoryPlan,recovery.Entries);
                    __0=bytes;__state=new(changed,recovery.Recovered,recovery.Preserved,recovery.Items);
                }
            }
            catch(Exception e)
            {
                owner.loadedRefused++;owner.loadedLastIssue=e.Message;
                try
                {
                    var field=BlockIdChunkCodec.Fields(__0).FirstOrDefault(f=>f.Tag==8&&f.Wire==2&&f.Length<=2*1024*1024);
                    if(field!=null)
                    {
                        var entity=owner.loadedCodec.EntityAtBytes(__0,field).Summary;
                        if(entity.X.HasValue&&entity.Y.HasValue&&entity.Z.HasValue&&entity.Dimension.HasValue)
                            owner.loadedLastIssue+=$" Near retained block {entity.X}, {entity.Y}, {entity.Z}, dimension {entity.Dimension} (in the refused chunk).";
                    }
                }
                catch{} // Diagnostic location must never affect chunk loading.
                // One log initially and at powers of two: a damaged region cannot
                // flood logs, but its refusal remains visible in the panel.
                if((owner.loadedRefused&(owner.loadedRefused-1))==0)owner.api.Logger.Warning("[BlockMigration] Skipped unsafe chunk ({0} refusals): {1}",owner.loadedRefused,e.Message);
            }
        }
    }
    private static void AfterLoadedChunk(ServerChunk __result,LoadedChange? __state)
    {
        if(__state==null||__result==null)return;
        __result.MarkModified();var owner=loadedOwner;if(owner==null)return;
        lock(owner.loadedGate){owner.loadedChanged++;owner.loadedBlocks+=__state.Blocks;owner.loadedInventoriesRecovered+=__state.Recovered;owner.loadedInventoriesPreserved+=__state.Preserved;owner.loadedInventoryItems+=__state.Items;}
    }
    private void DisposeLoadedMigration()
    {
        lock(loadedGate)
        {
            StopLoadedTerrainSweep();loadedActive=false;loadedHarmony?.UnpatchAll("newfies.suite.loaded-block-migration");loadedHarmony=null;
            if(loadedOwner==this)loadedOwner=null;loadedPool?.FreeAll();loadedPool=null;loadedCodec=null;
        }
    }
}
