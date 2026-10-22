using Vintagestory.API.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private Task? liveActivationWorker;
    private CancellationTokenSource? liveActivationCancel;
    private long liveActivationGeneration;
    private void CancelLiveActivation()
    {
        liveActivationGeneration++;
        var previous=liveActivationCancel;liveActivationCancel=null;
        if(previous==null)return;
        previous.Cancel();
        if(liveActivationWorker is{IsCompleted:false} running)_=running.ContinueWith(_=>previous.Dispose());else previous.Dispose();
    }
    // Called on the server thread, under gate. Backups are serialized separately
    // from scans; replacing/disabling a rule cancels obsolete backup work.
    private void QueueLiveActivation()
    {
        CancelLiveActivation();
        var plan=loadedMigration.Plan;
        if(disposed||!loadedMigration.Enabled||plan==null)return;
        if(plan.Inventory.RequiresPreparation&&plan.InventoryReport.State!="complete")return;
        try
        {
            Guard();Validate(plan);
            if(pending!=null||restorePending!=null||selectedPending!=null)throw new InvalidOperationException("Cancel other staged repairs before enabling migration.");
            string worldPath=Database().DatabaseFilename,backup=Path.Combine(directory,"world-before-loaded-"+plan.Id+".vcdbs");
            var cancellation=new CancellationTokenSource();liveActivationCancel=cancellation;var token=cancellation.Token;long generation=liveActivationGeneration;
            var previous=liveActivationWorker;
            lock(loadedGate)
            {
                loadedActive=false;loadedMigration.State=plan.CreateBackup?"backing-up":"activating";
                loadedMigration.Message=plan.CreateBackup?"Creating and verifying a saved-world backup in the background. Migration enables automatically when ready; no restart needed.":"Enabling migration automatically; manual-backup acknowledgement retained. No restart needed.";
                Write(LoadedMigrationFile,loadedMigration);
            }
            liveActivationWorker=Task.Run(async()=>
            {
                try
                {
                    if(previous!=null)try{await previous.ConfigureAwait(false);}catch{}
                    token.ThrowIfCancellationRequested();
                    if(plan.CreateBackup)
                    {
                        if(File.Exists(backup))MigrationWorldBackup.Verify(backup,token);
                        else MigrationWorldBackup.Create(worldPath,backup,token);
                    }
                    token.ThrowIfCancellationRequested();
                    api.Event.EnqueueMainThreadTask(()=>
                    {
                        lock(gate)
                        {
                            if(disposed||token.IsCancellationRequested||generation!=liveActivationGeneration||!loadedMigration.Enabled||loadedMigration.Plan?.Id!=plan.Id)return;
                            if(plan.CreateBackup)plan.Backup=backup;
                            ActivateLoadedMigration(false);
                        }
                    },"block-migration-live-activation");
                }
                catch(OperationCanceledException){}
                catch(Exception error)
                {
                    if(disposed||token.IsCancellationRequested)return;
                    api.Event.EnqueueMainThreadTask(()=>
                    {
                        lock(gate)lock(loadedGate)
                        {
                            if(disposed||token.IsCancellationRequested||generation!=liveActivationGeneration||loadedMigration.Plan?.Id!=plan.Id||!loadedMigration.Enabled)return;
                            loadedActive=false;loadedMigration.State="failed";loadedMigration.Message="Automatic activation failed: "+error.Message+" No new migrations enabled.";Write(LoadedMigrationFile,loadedMigration);
                            api.Logger.Error("[BlockMigration] {0}",loadedMigration.Message);
                        }
                    },"block-migration-activation-failed");
                }
            });
        }
        catch(Exception error)
        {
            lock(loadedGate){loadedActive=false;loadedMigration.State="failed";loadedMigration.Message="Automatic activation failed: "+error.Message;Write(LoadedMigrationFile,loadedMigration);}
        }
    }
    private void ResumeLiveMigration()
    {
        lock(gate)
        {
            if(disposed||loadedActive||!loadedMigration.Enabled||loadedMigration.Plan==null)return;
            var plan=loadedMigration.Plan;
            if(plan.Inventory.RequiresPreparation&&plan.InventoryReport.State!="complete")
            {
                try{Guard();Validate(plan,false);StartOrQueueMigrationInventoryPreparation(plan);}
                catch(Exception error){lock(loadedGate){loadedMigration.State="preparation-failed";loadedMigration.Message=error.Message;Write(LoadedMigrationFile,loadedMigration);}}
            }
            else QueueLiveActivation();
        }
    }
}
