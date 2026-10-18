namespace BlockLogger.WorldRepair;

public sealed partial class BlockIdRepairService
{
    public sealed class MappingAdvice
    {
        public int SourceId {get;set;}
        public string SourceCode {get;set;}="";
        public int LocationsChecked {get;set;}
        public List<string> Notes {get;set;}=[];
        public List<BlockRemapAdvisor.Candidate> Candidates {get;set;}=[];
    }
    public sealed class MappingAdviceReport
    {
        public string State {get;set;}="running";
        public string ScanId {get;set;}="";
        public List<MappingAdvice> Findings {get;set;}=[];
        public string Message {get;set;}="Reading bounded evidence for selected IDs. Nothing will be selected, staged or applied automatically.";
    }
    private MappingAdviceReport? mappingAdvice;
    public object StartMappingAdvice(IReadOnlyList<int> sources)
    {
        Guard();lock(gate)
        {
            if(worker is{IsCompleted:false})throw new InvalidOperationException("Wait for the current scan/inspection, or cancel it first.");
            if(scan?.State!="complete"||scan.Errors!=0||scan.Unsupported!=0)throw new InvalidOperationException("Finish a clean saved-map scan before requesting mapping recommendations.");
            if(sources.Count is <1 or >32||sources.Distinct().Count()!=sources.Count)throw new ArgumentException("Select 1–32 distinct scanned IDs.");
            var selected=sources.Select(id=>Clone(scan.Findings.SingleOrDefault(f=>f.Id==id&&f.Count>0)??throw new ArgumentException("An ID is not in this completed scan."))).ToList();
            var targets=api.World.Blocks.Where(b=>b?.Code!=null&&!b.IsMissing).Select(b=>new BlockRemapAdvisor.Target(b.BlockId,b.Code.ToString(),b.ForFluidsLayer,b.EntityClass??"")).ToList();
            var historyTargets=targets.GroupBy(t=>t.Code,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>new BlockHistoryAdvisor.Target(g.First().Id,g.First().EntityClass),StringComparer.Ordinal);
            string path=historyDatabasePath??"",world=api.World.SavegameIdentifier;
            var report=new MappingAdviceReport{ScanId=scan.Id};mappingAdvice=new(){ScanId=scan.Id};
            cancel?.Dispose();cancel=new();cancel.CancelAfter(TimeSpan.FromSeconds(15));var token=cancel.Token;
            worker=Task.Run(()=>{
                try
                {
                    foreach(var finding in selected)
                    {
                        token.ThrowIfCancellationRequested();var advice=new MappingAdvice{SourceId=finding.Id,SourceCode=finding.Code};var observations=new List<BlockRemapAdvisor.Observation>();
                        foreach(var pos in finding.Positions.Take(2))
                        {
                            token.ThrowIfCancellationRequested();var evidence=new BlockHistoryAdvisor.Report{World=world,X=pos.X,Y=pos.Y,Z=pos.Z,Dimension=pos.Dimension,BeforeUtc=DateTime.UtcNow};
                            try
                            {
                                BlockHistoryAdvisor.Read(path,evidence,historyTargets,token);advice.LocationsChecked++;
                                observations.AddRange(evidence.Candidates.Select(c=>new BlockRemapAdvisor.Observation($"{pos.X}, {pos.Y}, {pos.Z}, dimension {pos.Dimension}",c)));
                                if(evidence.Limited||evidence.State!="complete"||evidence.Candidates.Count==0)advice.Notes.Add(evidence.Message+(evidence.Limited?" History row limit reached.":""));
                            }
                            catch(OperationCanceledException){throw;}
                            catch(Exception e){advice.Notes.Add("BlockLogger evidence unavailable for this sample: "+e.Message);break;}
                        }
                        if(finding.Positions.Count==0)advice.Notes.Add("No sampled solid location is available; registry/name evidence only. Rescan to collect locations for solid blocks.");
                        advice.Candidates=BlockRemapAdvisor.Rank(finding.Id,finding.Code,finding.Solid,finding.Fluid,finding.Decor,targets,observations);report.Findings.Add(advice);
                    }
                    report.State="complete";report.Message="Recommendations ready. Pick a suggestion or enter a custom ID/code for each checked source. Sample evidence cannot establish the identity of every occurrence. Nothing has been staged.";
                }
                catch(OperationCanceledException){report.State="cancelled";report.Message="Recommendation lookup cancelled or reached its 15-second budget. Completed entries are partial; nothing was staged.";}
                catch(Exception e){report.State="failed";report.Message="Recommendation lookup failed: "+e.Message;}
                finally{lock(gate){mappingAdvice=report;}}
            });return new{success=true,scanId=scan.Id};
        }
    }
}
