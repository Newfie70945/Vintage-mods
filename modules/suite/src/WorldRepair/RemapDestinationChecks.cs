namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed record DestinationCheck(int SourceId,string Destination,bool Valid,int? TargetId,string TargetCode,string Message);
    private RepairMapping ResolveMapping(int source,int target,Scan current,Dictionary<int,string> map,bool allowAir,bool allowValid,bool automatic=false,MigrationInventoryOptions? inventory=null)
    {
        var finding=current.Findings.SingleOrDefault(f=>f.Id==source&&f.Count>0)??throw new ArgumentException("Source ID has no missing-block occurrences in the completed scan.");
        var block=Missing(target)?null:api.World.Blocks[target];
        var info=block==null?null:new BlockRemapAdvisor.Target(target,block.Code.ToString(),block.ForFluidsLayer,block.EntityClass??"");
        if(automatic&&(block==null||!Missing(source)||target==0||BlockIdChunkCodec.Canonical(map.GetValueOrDefault(source,""))!=block.Code.ToString()||finding.AutomaticIssue.Length>0))throw new ArgumentException("Exact identity recovery is unavailable or has incompatible retained metadata.");
        string issue=RemapTargetPolicy.Issue(new(source,Missing(source),finding.Solid,finding.Fluid,finding.Decor),info!=null&&((BlockIdChunkCodec.Canonical(map.GetValueOrDefault(source,""))==info.Code&&finding.AutomaticIssue.Length==0)||(inventory?.Mode!="preserve"&&inventory!=null&&info.Code.StartsWith("game:",StringComparison.Ordinal)&&SupportedContainer(info.EntityClass)))?info with{EntityClass=""}:info,allowAir,allowValid);
        if(issue.Length>0)throw new ArgumentException(issue);
        return new(){Source=source,Target=target,SourceCode=Label(source,map),TargetCode=info!.Code,Count=finding.Count,Chunks=finding.Chunks};
    }
    public object CheckDestinations(IReadOnlyList<RemapChoice> choices,bool allowAir,bool allowValid)
    {
        Guard();lock(gate)
        {
            if(scan?.State!="complete"||scan.Errors!=0||scan.Unsupported!=0)throw new InvalidOperationException("Finish an error-free saved-map scan before checking destinations.");
            if(choices.Count is <1 or >32||choices.Any(c=>c==null)||choices.Select(c=>c.SourceId).Distinct().Count()!=choices.Count)throw new ArgumentException("Check 1–32 distinct source IDs.");
            var map=Mapping();var checks=new List<DestinationCheck>();
            foreach(var choice in choices)
            {
                try
                {
                    if(string.IsNullOrWhiteSpace(choice.Destination)||choice.Destination.Length>256)throw new ArgumentException("Enter a destination ID or exact domain:code.");
                    var m=ResolveMapping(choice.SourceId,ResolveTarget(choice.Destination),scan,map,allowAir,allowValid);
                    checks.Add(new(choice.SourceId,choice.Destination.Trim(),true,m.Target,m.TargetCode,"Destination resolves and matches the source layers. Retained metadata is verified independently as each affected chunk loads."));
                }
                catch(Exception e)when(e is ArgumentException or InvalidOperationException)
                {checks.Add(new(choice.SourceId,choice.Destination??"",false,null,"",e.Message));}
            }
            return new{scanId=scan.Id,checks};
        }
    }
}
