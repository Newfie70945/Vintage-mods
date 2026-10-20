using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private string ReplaceLookedAtHeld(IServerPlayer player)
        =>ReplaceLookedAtHeldOptions(player,"");
    private string ReplaceLookedAtHeldOptions(IServerPlayer player,string arguments)=>ReviewHeldReplacement(player,arguments,false,false);
    private string ReviewHeldReplacement(IServerPlayer player,string arguments,bool previewOnly,bool single)
    {
        var tokens=arguments.Split(' ',StringSplitOptions.RemoveEmptyEntries);var previous=loadedMigration.Enabled?loadedMigration.Plan?.Inventory:null;
        string mode=tokens.FirstOrDefault(t=>t is "preserve" or "reconnect" or "recover-empty" or "replace")??(single?"preserve":previous?.Mode)??"preserve";
        if(tokens.Any(t=>t is not ("preserve" or "reconnect" or "recover-empty" or "replace" or "confirm-inventory-risk" or "confirm-overwrite" or "no-backup" or "manual-copy-confirmed" or "confirm-risk")&&!t.StartsWith("before=",StringComparison.Ordinal)&&!t.StartsWith("source=",StringComparison.Ordinal)&&!t.StartsWith("at=",StringComparison.Ordinal)&&!t.StartsWith("dimension=",StringComparison.Ordinal)))throw new ArgumentException("Options: reconnect, recover-empty or replace; source=ID; at=X,Y,Z dimension=N; confirm-inventory-risk; confirm-overwrite for replace; before=ISO-UTC-time; no-backup manual-copy-confirmed.");
        foreach(var prefix in new[]{"source=","at=","dimension=","before="})if(tokens.Count(t=>t.StartsWith(prefix,StringComparison.Ordinal))>1)throw new ArgumentException("Specify "+prefix+" only once.");
        if(tokens.Count(t=>t is "preserve" or "reconnect" or "recover-empty" or "replace")>1)throw new ArgumentException("Choose exactly one inventory mode.");
        bool createBackup=!tokens.Contains("no-backup"),manualCopy=tokens.Contains("manual-copy-confirmed");
        if(!previewOnly&&!single&&!createBackup&&!manualCopy)throw new ArgumentException("Skipping automatic backup requires no-backup manual-copy-confirmed. Make a manual world copy first; migration is experimental and can lose data.");
        string before=tokens.FirstOrDefault(t=>t.StartsWith("before=",StringComparison.Ordinal))?[7..]??previous?.BeforeUtc?.ToString("O")??"";
        var inventory=MigrationInventoryOptions.Parse(mode,before,tokens.Contains("confirm-inventory-risk")||previous?.RiskAcknowledged==true,tokens.Contains("confirm-overwrite")||previous?.OverwriteAcknowledged==true);
        Guard();if(single&&(pending!=null||restorePending!=null||selectedPending!=null))throw new InvalidOperationException("Cancel staged repair jobs before changing one block.");var pos=MigrationTargetPosition(player,tokens);
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
        string inventoryNote="Inventory: no historical contents were inspected or restored. "+(api.World.BlockAccessor.GetBlockEntity(pos)==null?"No live block entity is present; saved metadata may still exist and will be verified by migration.":"A live block entity survives; rule activation still checks its compatibility. Existing contents are not a promise of historical recovery.");
        if(previewOnly)return $"READ-ONLY preview at {pos.X},{pos.Y},{pos.Z}, dimension {pos.dimension}: missing {(materialSource?"chiseled material":"solid block")} ID {source} ({Label(source,map)}) → {target!.Code} (ID {target.Id}). Inventory mode: {inventory.Mode}. {inventoryNote} No blocks, rules, backups or held items changed. replaceheld saves an ID-wide rule; repairheld changes only one plain missing block after confirmation.";
        if(single)
        {
            if(!tokens.Contains("confirm-risk")||!manualCopy)throw new ArgumentException("Single-block repair requires repairheld confirm-risk manual-copy-confirmed. Make a manual world copy first. No automatic backup will be made.");
            if(materialSource||api.World.BlockAccessor.GetBlockEntity(pos)!=null||!string.IsNullOrEmpty(target!.EntityClass)||inventory.Mode!="preserve")throw new InvalidOperationException("Single-block repair supports plain missing blocks only. Containers/retained metadata and chiseled materials require a reviewed migration rule; nothing changed.");
            if(chunk is not Vintagestory.Server.ServerChunk serverChunk)throw new NotSupportedException("Cannot verify this chunk implementation; nothing changed.");
            var bytes=serverChunk.ToBytes();var fields=BlockIdChunkCodec.Fields(bytes).Where(f=>f.Tag==8).ToArray();
            if(fields.Length>32768||fields.Sum(f=>(long)f.Length)>32*1024*1024)throw new InvalidDataException("Retained metadata exceeds verification limits; nothing changed.");
            foreach(var field in fields)
            {
                if(field.Wire!=2||field.Length>2*1024*1024)throw new InvalidDataException("Cannot verify retained metadata; nothing changed.");
                var saved=SavedBlockEntityInspector.Inspect(bytes.AsSpan(field.Data,field.Length).ToArray());
                if(!saved.Complete)throw new InvalidDataException("Unverifiable retained metadata; nothing changed.");
                if(saved.X==pos.X&&saved.Y==pos.Y&&saved.Z==pos.Z&&saved.Dimension==pos.dimension)throw new InvalidOperationException("Selected cell retains serialized metadata; use a compatible migration rule instead.");
            }
            if(api.World.BlockAccessor.GetBlockId(pos)!=source)throw new InvalidOperationException("Target changed; inspect it again.");
            api.World.BlockAccessor.ExchangeBlock(target.Id,pos);api.World.BlockAccessor.MarkBlockDirty(pos);chunk.MarkModified();
            api.Logger.Notification("[BlockMigration] {0} repaired one plain missing block {1} -> {2} at {3} without creating a rule or automatic backup.",player.PlayerUID,source,target.Code,pos);
            return $"Repaired exactly one plain missing block at {pos.X},{pos.Y},{pos.Z}, dimension {pos.dimension}: ID {source} → {target.Code} (ID {target.Id}). No ID-wide rule was added. No inventory restored, automatic backup created or held item consumed. Normal world saving persists this change.";
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
