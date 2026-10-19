namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed record AutomaticSkip(int SourceId,string SavedCode,long Count,string Reason);
    public Plan PreviewAutomatic(bool createBackup=true,bool manualBackupAcknowledged=false)
    {
        Guard();lock(gate)
        {
            if(scan?.State!="complete"||scan.Errors!=0||scan.Unsupported!=0)throw new InvalidOperationException("Finish an error-free saved-map scan first.");
            if(scan.Registry!=Fingerprint(Mapping()))throw new InvalidOperationException("Registry changed; scan again.");
            var choices=new List<RemapChoice>();var skipped=new List<AutomaticSkip>();var map=Mapping();
            foreach(var finding in scan.Findings.Where(f=>f.Count>0))
            {
                string reason="";int? target=finding.SameCodeRegisteredId;
                if(!Missing(finding.Id))reason="Healthy registered source is protected.";
                else if(string.IsNullOrEmpty(map.GetValueOrDefault(finding.Id)))reason="Original saved code is unavailable. Use location history or a backup; no replacement can be inferred automatically.";
                else if(!target.HasValue||target.Value==0||target.Value==finding.Id||Missing(target.Value)||BlockIdChunkCodec.Canonical(map[finding.Id])!=api.World.Blocks[target.Value].Code.ToString())reason="Original asset is not installed. Restore its mod or choose a replacement manually.";
                else if(finding.AutomaticIssue.Length>0)reason=finding.AutomaticIssue;
                if(reason.Length>0)skipped.Add(new(finding.Id,finding.Code,finding.Count,reason));
                else choices.Add(new(finding.Id,api.World.Blocks[target!.Value].Code.ToString()));
            }
            if(choices.Count==0)throw new InvalidOperationException("No exact installed identities can be migrated automatically. "+string.Join(" ",skipped.Select(s=>s.Reason).Distinct().Take(3)));
            var plan=PreviewMappings(choices,false,false,createBackup,manualBackupAcknowledged,true,true);plan.Skipped=skipped;
            plan.Message=$"Automatic exact-code recovery: {plan.Mappings.Count} missing IDs / {plan.Count} saved occurrences. {skipped.Count} IDs excluded and listed for manual review. Healthy source definitions are protected. Compatible retained block-entity bytes are preserved, including inventory contents already present. Missing/deleted metadata and inventory stack IDs are not reconstructed. Review the mappings, then stage for restart. "+(createBackup?"Automatic world backup enabled.":"Automatic backup disabled; retain your acknowledged manual world copy.");
            return plan;
        }
    }
}
