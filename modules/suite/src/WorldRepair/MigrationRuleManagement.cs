namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private Vintagestory.API.Common.TextCommandResult ListMigrationRules(int page)
    {
        lock(loadedGate)
        {
            var rules=loadedMigration.Plan==null?[]:EffectiveMappings(loadedMigration.Plan);int pages=Math.Max(1,(rules.Count+7)/8);page=Math.Clamp(page,1,pages);
            string text=$"Migration rules: {rules.Count}; {loadedMigration.State}. Page {page}/{pages}.";
            foreach(var rule in rules.Skip((page-1)*8).Take(8))text+=$"\nID {rule.Source} -> {rule.TargetCode[..Math.Min(160,rule.TargetCode.Length)]} (ID {rule.Target})";
            if(rules.Count==0)text+=" No rules saved. Look at a missing block while holding its replacement and use /nseblockmigrate replaceheld.";
            return Vintagestory.API.Common.TextCommandResult.Success(BlockLogger.Commands.RollbackChatText.Encode(text));
        }
    }
    private List<RepairMapping> CopyConfiguredRules()
    {
        if(loadedMigration.Plan==null)return [];
        Validate(loadedMigration.Plan,false);
        return EffectiveMappings(loadedMigration.Plan).Select(m=>new RepairMapping{Source=m.Source,Target=m.Target,SourceCode=m.SourceCode,TargetCode=m.TargetCode,Count=0,Chunks=0}).ToList();
    }
    private Plan SaveConfiguredRules(List<RepairMapping> rules,MigrationInventoryOptions inventory,bool createBackup=true,bool manualCopy=false,bool allowAir=false)
    {
        if(rules.Count==0)throw new ArgumentException("Configure at least one source ID, or clear the rules.");
        var first=rules[0];var plan=new Plan{Inventory=inventory,LoadedOnly=true,Mappings=rules,Source=first.Source,Target=first.Target,SourceCode=first.SourceCode,TargetCode=first.TargetCode,World=api.World.SavegameIdentifier,Registry=Fingerprint(Mapping()),ScanId="rules-"+Guid.NewGuid().ToString("N"),CreateBackup=createBackup,ManualBackupAcknowledged=manualCopy,AllowAir=allowAir||loadedMigration.Plan?.AllowAir==true,Message="Explicit ID rules saved without a world scan. Occurrence counts are unknown until chunks load."};
        if(inventory.RequiresPreparation){plan.InventoryRegistry=RestoreRegistry();plan.InventoryReport=new(){State="queued",Message="Rule saved. Optional inventory evidence preparation is queued separately; no world edits made."};}
        Validate(plan,false);SaveLoadedConfiguration(plan);preview=null;
        if(inventory.RequiresPreparation)
        {
            try{StartOrQueueMigrationInventoryPreparation(plan);}
            catch(Exception e){plan.InventoryReport=new(){State="failed",Message=e.Message};loadedMigration.State="preparation-failed";loadedMigration.Message="Rules remain saved; inventory preparation failed. "+e.Message;Write(LoadedMigrationFile,loadedMigration);}
        }
        return plan;
    }
    public object ConfigureMigrationRule(int source,string destination,MigrationInventoryOptions? inventory=null,bool createBackup=true,bool manualCopy=false,bool allowAir=false)
    {
        Guard();lock(gate)
        {
            if(source<=0||!Missing(source))throw new ArgumentException("Only a missing non-air source ID can be configured. Healthy registered source IDs are protected.");
            int target=ResolveTarget(destination);if(target==0&&!allowAir)throw new ArgumentException("Replacing with air needs the explicit Allow Air option.");if(Missing(target)||target==source)throw new ArgumentException("Choose a different registered destination.");
            var rules=CopyConfiguredRules();rules.RemoveAll(m=>m.Source==source);rules.Add(new(){Source=source,Target=target,SourceCode=Label(source,Mapping()),TargetCode=api.World.Blocks[target].Code.ToString()});
            SaveConfiguredRules(rules,inventory??loadedMigration.Plan?.Inventory??new(),createBackup,manualCopy,allowAir);return LoadedMigrationSnapshot();
        }
    }
    public object RemoveMigrationRule(int source)
    {
        Guard();lock(gate)
        {
            var rules=CopyConfiguredRules();if(rules.RemoveAll(m=>m.Source==source)==0)throw new ArgumentException("Source ID is not configured.");
            if(rules.Count==0)return ClearMigrationRules();
            var old=loadedMigration.Plan!;SaveConfiguredRules(rules,old.Inventory,old.CreateBackup,old.ManualBackupAcknowledged);return LoadedMigrationSnapshot();
        }
    }
    public object ResumeConfiguredMigration()
    {
        Guard();lock(gate)
        {
            var plan=loadedMigration.Plan??throw new InvalidOperationException("No saved migration rules. Save a rule first.");
            Validate(plan,false);
            if(loadedActive)return LoadedMigrationSnapshot();
            lock(loadedGate)
            {
                loadedMigration.Enabled=true;loadedMigration.State="activating";
                loadedMigration.Message="Resuming saved rules using their stored backup and inventory options.";
                Write(LoadedMigrationFile,loadedMigration);
            }
            ResumeLiveMigration();return LoadedMigrationSnapshot();
        }
    }
    public object EnableConfiguredWithoutBackup(bool manualCopy)
    {
        Guard();lock(gate)
        {
            if(!manualCopy)throw new InvalidOperationException("Make a manual world copy and acknowledge it before bypassing automatic backup. Migration is experimental and can lose data.");
            var plan=loadedMigration.Plan??throw new InvalidOperationException("No saved rules. Save a migration rule first.");
            Validate(plan,false);
            if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Cancel other staged repairs before enabling migration.");
            lock(loadedGate)
            {
                CancelLiveActivation();StopLoadedTerrainSweep();loadedActive=false;
                plan.CreateBackup=false;plan.ManualBackupAcknowledged=true;plan.Backup="";
                loadedMigration.Enabled=true;loadedMigration.State="activating";
                loadedMigration.Message="Saved rules enabled with acknowledged manual backup. Automatic world backup disabled for this saved rule set, including future restarts. Inventory compatibility/preparation checks remain required.";
                Write(LoadedMigrationFile,loadedMigration);
            }
            // Do not queue behind a cancelled backup worker. Its generation is
            // invalidated, so late completion cannot replace this saved policy.
            if(plan.Inventory.RequiresPreparation&&plan.InventoryReport.State!="complete")ResumeLiveMigration();
            else
            {
                ActivateLoadedMigration(false);
                if(!loadedActive)throw new InvalidOperationException(loadedMigration.Message);
            }
            return LoadedMigrationSnapshot();
        }
    }
    public object ClearMigrationRules()
    {
        lock(gate)lock(loadedGate)
        {
            CancelLiveActivation();CancelMigrationInventoryPreparation();StopMigrationRuntime();loadedMigration=new(){State="disabled",Message="Migration disabled and configured rules cleared. Saved terrain was not reverted."};Write(LoadedMigrationFile,loadedMigration);return LoadedMigrationSnapshot();
        }
    }
}
