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
        string mode=tokens.FirstOrDefault(t=>t is "preserve" or "recover-empty" or "replace")??previous?.Mode??"preserve";
        if(tokens.Any(t=>t is not ("preserve" or "recover-empty" or "replace" or "confirm-inventory-risk" or "confirm-overwrite" or "no-backup" or "manual-copy-confirmed")&&!t.StartsWith("before=",StringComparison.Ordinal)))throw new ArgumentException("Options: recover-empty or replace; confirm-inventory-risk; confirm-overwrite for replace; before=ISO-UTC-time; no-backup manual-copy-confirmed.");
        bool createBackup=!tokens.Contains("no-backup"),manualCopy=tokens.Contains("manual-copy-confirmed");
        if(!createBackup&&!manualCopy)throw new ArgumentException("Skipping automatic backup requires no-backup manual-copy-confirmed. Make a manual world copy first; migration is experimental and can lose data.");
        string before=tokens.FirstOrDefault(t=>t.StartsWith("before=",StringComparison.Ordinal))?[7..]??previous?.BeforeUtc?.ToString("O")??"";
        var inventory=MigrationInventoryOptions.Parse(mode,before,tokens.Contains("confirm-inventory-risk")||previous?.RiskAcknowledged==true,tokens.Contains("confirm-overwrite")||previous?.OverwriteAcknowledged==true);
        Guard();var pos=player.CurrentBlockSelection?.Position?.Copy()??throw new ArgumentException("Look directly at a missing block whose ID you want to migrate.");
        var stack=player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        bool held=stack?.Class==EnumItemClass.Block;var target=held?stack?.Block:null;
        var chunk=api.World.BlockAccessor.GetChunkAtBlockPos(pos)??throw new InvalidOperationException("Selected chunk is not loaded.");
        int index=((pos.Y&31)*32+(pos.Z&31))*32+(pos.X&31);int source=chunk.UnpackAndReadBlock(index,BlockLayersAccess.Solid);
        var map=Mapping();bool exact=target?.Code!=null&&BlockIdChunkCodec.Canonical(map.GetValueOrDefault(source,""))==target.Code.ToString();
        string issue=HeldBlockReplacementPolicy.Issue(source,Missing(source),held,target?.Id??0,target==null||Missing(target.Id),target?.ForFluidsLayer==true||chunk.UnpackAndReadBlock(index,BlockLayersAccess.Fluid)!=0,!string.IsNullOrEmpty(target?.EntityClass)&&!exact&&(inventory.Mode=="preserve"||target?.Code?.Domain!="game"||!SupportedContainer(target.EntityClass)),false);
        if(issue.Length>0)throw new InvalidOperationException(issue);
        lock(gate)
        {
            var rules=CopyConfiguredRules();rules.RemoveAll(m=>m.Source==source);rules.Add(new(){Source=source,Target=target!.Id,SourceCode=Label(source,map),TargetCode=target.Code.ToString()});
            SaveConfiguredRules(rules,inventory,createBackup,manualCopy);
            api.Logger.Notification("[BlockMigration] {0} saved ID-wide rule {1} -> {2}; {3} configured rules, no world scan required.",player.PlayerUID,source,target.Code,rules.Count);
            return $"Saved migration rule for ALL missing blocks with ID {source} → {target.Code} (ID {target.Id}). {rules.Count} rules retained; no world scan required. "+(inventory.Mode=="preserve"?(createBackup?"Backup and activation start automatically; no restart needed.":"Automatic backup skipped by explicit acknowledgement; activation starts automatically.")+" Safe already-loaded solid blocks are processed in a bounded background pass on the server thread; incompatible containers require reloading.":"Optional inventory recovery is preparing separately. "+(createBackup?"Backup then activation follow automatically.":"No automatic world backup will be created.")+" See Block ID Migration → Migration Rules.")+" Your held stack was not consumed.";
        }
    }
}
