using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private string ReplaceLookedAtHeld(IServerPlayer player)
        =>ReplaceLookedAtHeldOptions(player,"");
    private string ReplaceLookedAtHeldOptions(IServerPlayer player,string arguments)
    {
        var tokens=arguments.Split(' ',StringSplitOptions.RemoveEmptyEntries);var previous=loadedMigration.Enabled?loadedMigration.Plan?.Inventory:null;
        string mode=tokens.FirstOrDefault(t=>t is "preserve" or "reconnect" or "recover-empty" or "replace")??previous?.Mode??"preserve";
        if(tokens.Any(t=>t is not ("preserve" or "reconnect" or "recover-empty" or "replace" or "confirm-inventory-risk" or "confirm-overwrite" or "no-backup" or "manual-copy-confirmed")&&!t.StartsWith("before=",StringComparison.Ordinal)&&!t.StartsWith("source=",StringComparison.Ordinal)&&!t.StartsWith("at=",StringComparison.Ordinal)&&!t.StartsWith("dimension=",StringComparison.Ordinal)))throw new ArgumentException("Options: reconnect, recover-empty or replace; source=ID; at=X,Y,Z dimension=N; confirm-inventory-risk; confirm-overwrite for replace; before=ISO-UTC-time; no-backup manual-copy-confirmed.");
        foreach(var prefix in new[]{"source=","at=","dimension=","before="})if(tokens.Count(t=>t.StartsWith(prefix,StringComparison.Ordinal))>1)throw new ArgumentException("Specify "+prefix+" only once.");
        if(tokens.Count(t=>t is "preserve" or "reconnect" or "recover-empty" or "replace")>1)throw new ArgumentException("Choose exactly one inventory mode.");
        bool createBackup=!tokens.Contains("no-backup"),manualCopy=tokens.Contains("manual-copy-confirmed");
        if(!createBackup&&!manualCopy)throw new ArgumentException("Skipping automatic backup requires no-backup manual-copy-confirmed. Make a manual world copy first; migration is experimental and can lose data.");
        string before=tokens.FirstOrDefault(t=>t.StartsWith("before=",StringComparison.Ordinal))?[7..]??previous?.BeforeUtc?.ToString("O")??"";
        var inventory=MigrationInventoryOptions.Parse(mode,before,tokens.Contains("confirm-inventory-risk")||previous?.RiskAcknowledged==true,tokens.Contains("confirm-overwrite")||previous?.OverwriteAcknowledged==true);
        Guard();var pos=MigrationTargetPosition(player,tokens);
        var stack=player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        bool held=stack?.Class==EnumItemClass.Block;var target=held?stack?.Block:null;
        var chunk=api.World.BlockAccessor.GetChunkAtBlockPos(pos)??throw new InvalidOperationException("Selected chunk is not loaded.");
        int index=((pos.Y&31)*32+(pos.Z&31))*32+(pos.X&31);int source=chunk.UnpackAndReadBlock(index,BlockLayersAccess.Solid);
        bool materialSource=false;
        if(api.World.BlockAccessor.GetBlockEntity(pos) is Vintagestory.GameContent.BlockEntityMicroBlock micro)
        {if(micro.GetType()!=typeof(Vintagestory.GameContent.BlockEntityMicroBlock))throw new NotSupportedException("Custom chiseled block entity classes require independent compatibility verification.");source=SelectMissingMaterial(micro.BlockIds,tokens);materialSource=true;}
        else if(tokens.Any(t=>t.StartsWith("source=",StringComparison.Ordinal)))throw new ArgumentException("source=ID is only for choosing a missing material inside a chiseled block.");
        var map=Mapping();bool exact=target?.Code!=null&&BlockIdChunkCodec.Canonical(map.GetValueOrDefault(source,""))==target.Code.ToString();
        string issue=HeldBlockReplacementPolicy.Issue(source,Missing(source),held,target?.Id??0,target==null||Missing(target.Id),target?.ForFluidsLayer==true||chunk.UnpackAndReadBlock(index,BlockLayersAccess.Fluid)!=0,!string.IsNullOrEmpty(target?.EntityClass)&&!exact&&(inventory.Mode=="preserve"||target?.Code?.Domain!="game"||!SupportedContainer(target.EntityClass)),false);
        if(materialSource&&!string.IsNullOrEmpty(target?.EntityClass))throw new InvalidOperationException("Choose a solid building material without a block entity for chiseled materials.");
        if(issue.Length>0)
        {
            if(!string.IsNullOrEmpty(target?.EntityClass)&&!exact&&inventory.Mode=="preserve")
                issue+=" For a compatible bookshelf use /nseblockmigrate replaceheld reconnect confirm-inventory-risk (preserve current contents), or recover-empty confirm-inventory-risk (restore empty contents from history). Add no-backup manual-copy-confirmed to skip automatic backup.";
            throw new InvalidOperationException(issue);
        }
        lock(gate)
        {
            var rules=CopyConfiguredRules();rules.RemoveAll(m=>m.Source==source);rules.Add(new(){Source=source,Target=target!.Id,SourceCode=Label(source,map),TargetCode=target.Code.ToString()});
            SaveConfiguredRules(rules,inventory,createBackup,manualCopy);
            api.Logger.Notification("[BlockMigration] {0} saved ID-wide rule {1} -> {2}; {3} configured rules, no world scan required.",player.PlayerUID,source,target.Code,rules.Count);
            return $"Saved migration rule for ALL missing blocks/chiseled material references with ID {source} → {target.Code} (ID {target.Id}). {rules.Count} rules retained; no world scan required. "+(!inventory.RequiresPreparation?(createBackup?"Backup and activation start automatically; no restart needed.":"Automatic backup skipped by explicit acknowledgement; activation starts automatically.")+" Safe already-loaded solid blocks are processed in a bounded background pass on the server thread; reconnect checks compatible containers; unverifiable cells remain unchanged.":"Optional inventory recovery is preparing separately. "+(createBackup?"Backup then activation follow automatically.":"No automatic world backup will be created.")+" See Block ID Migration → Migration Rules.")+" Your held stack was not consumed.";
        }
    }
}
