namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    // Distinct ownership from the shared scan worker: pausing migration must
    // not cancel an unrelated administrator-requested block scan.
    private CancellationTokenSource? migrationInventoryCancel;
    private long migrationPreparationGeneration;
    private long queuedPreparationGeneration = -1;

    private void CancelMigrationInventoryPreparation()
    {
        migrationPreparationGeneration++;
        migrationInventoryCancel?.Cancel();
    }

    private void StartOrQueueMigrationInventoryPreparation(Plan plan)
    {
        if (worker is not { IsCompleted: false } running)
        {
            StartMigrationInventoryPreparation(plan);
            return;
        }
        long generation = migrationPreparationGeneration;
        if (queuedPreparationGeneration == generation) return;
        queuedPreparationGeneration = generation;
        loadedMigration.State = "preparation-queued";
        loadedMigration.Message = "Waiting for the current worker to stop before preparing the saved inventory rules.";
        Write(LoadedMigrationFile, loadedMigration);
        _ = running.ContinueWith(_ =>
        {
            if (disposed) return;
            api.Event.EnqueueMainThreadTask(() =>
            {
                lock (gate)
                {
                    if (disposed || generation != migrationPreparationGeneration || !loadedMigration.Enabled || loadedMigration.Plan?.Id != plan.Id) return;
                    queuedPreparationGeneration = -1;
                    ResumeLiveMigration();
                }
            }, "migration-resume-preparation");
        });
    }

    // Caller holds loadedGate. Already-entered chunk rewrites finish before
    // this runs; new calls exit before world access or taking loadedGate.
    private void StopMigrationRuntime()
    {
        loadedActive = false;
        if (ReferenceEquals(Volatile.Read(ref loadedOwner), this)) Volatile.Write(ref loadedOwner, null);
        StopLoadedTerrainSweep();
        loadedPool?.FreeAll(); loadedPool = null; loadedCodec = null;
        loadedReplacements.Clear(); loadedPlainReplacements.Clear(); loadedIdentities.Clear(); loadedFluids.Clear();
        loadedInventorySources.Clear(); loadedReconnectSources.Clear(); loadedReconnectCatalog = null;
    }
}
