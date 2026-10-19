using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    private string ReplaceLookedAtHeld(IServerPlayer player)
    {
        Guard();var pos=player.CurrentBlockSelection?.Position?.Copy()??throw new ArgumentException("Look directly at a missing block whose ID you want to migrate.");
        var stack=player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        bool held=stack?.Class==EnumItemClass.Block;var target=held?stack?.Block:null;
        var chunk=api.World.BlockAccessor.GetChunkAtBlockPos(pos)??throw new InvalidOperationException("Selected chunk is not loaded.");
        int index=((pos.Y&31)*32+(pos.Z&31))*32+(pos.X&31);int source=chunk.UnpackAndReadBlock(index,BlockLayersAccess.Solid);
        var map=Mapping();bool exact=target?.Code!=null&&BlockIdChunkCodec.Canonical(map.GetValueOrDefault(source,""))==target.Code.ToString();
        string issue=HeldBlockReplacementPolicy.Issue(source,Missing(source),held,target?.Id??0,target==null||Missing(target.Id),target?.ForFluidsLayer==true||chunk.UnpackAndReadBlock(index,BlockLayersAccess.Fluid)!=0,!string.IsNullOrEmpty(target?.EntityClass)&&!exact,false);
        if(issue.Length>0)throw new InvalidOperationException(issue);
        lock(gate)
        {
            if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for the active scan or cancel it before configuring a held-block migration.");
            if(loadedMigration.Enabled&&loadedMigration.Plan!=null)Validate(loadedMigration.Plan);
            var rules=loadedMigration.Enabled&&loadedMigration.Plan!=null?EffectiveMappings(loadedMigration.Plan).Select(m=>new RepairMapping{Source=m.Source,Target=m.Target,SourceCode=m.SourceCode,TargetCode=m.TargetCode,Count=m.Count,Chunks=m.Chunks}).ToDictionary(m=>m.Source):new Dictionary<int,RepairMapping>();
            rules[source]=new(){Source=source,Target=target!.Id,SourceCode=Label(source,map),TargetCode=target.Code.ToString()};
            var first=rules.Values.First();var plan=new Plan{LoadedOnly=true,Mappings=rules.Values.ToList(),Source=first.Source,Target=first.Target,SourceCode=first.SourceCode,TargetCode=first.TargetCode,World=api.World.SavegameIdentifier,Registry=Fingerprint(map),ScanId="held-"+Guid.NewGuid().ToString("N"),CreateBackup=true,Count=rules.Values.Sum(m=>m.Count),Message="Rules chosen from looked-at missing IDs and held replacement blocks. Counts are not a fresh whole-map census; every affected loaded chunk is checked independently."};
            Validate(plan);SaveLoadedConfiguration(plan);preview=null;
            api.Logger.Notification("[BlockMigration] {0} staged all-occurrence migration for missing ID {1} -> {2}; {3} configured rules, activation after restart.",player.PlayerUID,source,target.Code,rules.Count);
            return $"Staged migration for ALL missing blocks with ID {source} → {target.Code} (ID {target.Id}). {rules.Count} rule(s) retained. Restart once: a verified backup is created, then matching blocks migrate as their chunks load. Your held stack was not consumed.";
        }
    }
}
