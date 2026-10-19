namespace BlockLogger.WorldRepair;

// Registry identities and bounded exact-location history are separate evidence.
// Name similarity is an explicitly weak fallback, never an automatic mapping.
public static class BlockRemapAdvisor
{
    public sealed record Target(int Id,string Code,bool Fluid,string EntityClass);
    public sealed record Observation(string Location,BlockHistoryAdvisor.Candidate Candidate);
    public sealed class Candidate
    {
        public int Id {get;set;}
        public string Code {get;set;}="";
        public string Confidence {get;set;}="Low";
        public string Reason {get;set;}="";
        public bool Selectable {get;set;}
        public string Restriction {get;set;}="";
        public int Score {get;set;}
        public List<string> Evidence {get;set;}=[];
    }
    public static List<Candidate> Rank(int source,string savedCode,long solid,long fluid,long decor,IReadOnlyList<Target> targets,IReadOnlyList<Observation> history)
    {
        string code=Canonical(savedCode),domain=code.Split(':')[0],path=code.Contains(':')?code[(code.IndexOf(':')+1)..]:"";
        string family=path.Split('-')[0],domainPrefix=domain+":",familyPrefix=family+"-";
        var byCode=history.GroupBy(h=>h.Candidate.Code,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.Ordinal);
        var supportedCodes=history.Where(h=>h.Candidate.Confidence=="Medium").Select(h=>h.Candidate.Code).ToHashSet(StringComparer.Ordinal);
        var result=new List<Candidate>();
        foreach(var target in targets)
        {
            if(target.Id<=0||target.Id==source)continue;
            IReadOnlyList<Observation> observations=byCode.TryGetValue(target.Code,out var observed)?observed:Array.Empty<Observation>();
            bool exact=code.Length>0&&target.Code==code;
            var targetPath=target.Code.AsSpan(target.Code.IndexOf(':')+1);
            bool sameDomain=target.Code.StartsWith(domainPrefix,StringComparison.Ordinal);
            bool familyMatch=family.Length>2&&sameDomain&&(targetPath.SequenceEqual(family)||targetPath.StartsWith(familyPrefix,StringComparison.Ordinal));
            bool movedDomain=path.Length>0&&!sameDomain&&targetPath.SequenceEqual(path);
            if(!exact&&observations.Count==0&&!familyMatch&&!movedDomain)continue;
            bool supported=observations.Any(h=>h.Candidate.Confidence=="Medium");
            var c=new Candidate{Id=target.Id,Code=target.Code,Score=exact?90:supported?65:observations.Count>0?45:familyMatch?25:15,Confidence=exact||supported?"Medium":"Low",Reason=exact?"Exact saved block code is registered at another numeric ID. Verify duplicate/legacy identity before a global remap.":supported?"Supported by placement/observation history at sampled affected locations; this is not evidence for every occurrence.":observations.Count>0?"Older, removed or incomplete history at sampled locations; verify manually.":familyMatch?"Same mod domain and block-name family only. Variant/shape compatibility is unverified.":"Same path in a different mod domain only. This may be unrelated content."};
            if(exact)c.Evidence.Add("Saved registry code: "+code+"; current registered ID: "+target.Id+".");
            foreach(var observation in observations.Take(2))
            {
                c.Evidence.Add(observation.Location+": "+observation.Candidate.Reason);
                foreach(var e in observation.Candidate.Evidence.Take(2))c.Evidence.Add($"{e.Source} #{e.RecordId}, {e.Action}, {e.TimestampUtc:O}, {e.Code}");
            }
            bool conflicting=supportedCodes.Count>1||(supportedCodes.Count==1&&!supportedCodes.Contains(target.Code));
            if(conflicting){c.Confidence="Low";c.Score=Math.Min(c.Score,40);c.Reason+=" Sample histories contain a different plausible identity; no consensus.";}
            bool layer=(fluid==0||target.Fluid)&&((solid==0&&decor==0)||!target.Fluid);
            c.Selectable=layer&&string.IsNullOrEmpty(target.EntityClass);
            c.Restriction=!string.IsNullOrEmpty(target.EntityClass)?"Block entity/inventory: requires exact-code migration with compatible retained metadata.":!layer?"Destination does not match the saved solid/fluid layers.":"";
            if(familyMatch&&!exact&&observations.Count==0)
            {
                var originalTokens=path.Split('-').ToHashSet(StringComparer.Ordinal);var variantTokens=targetPath.ToString().Split('-').ToHashSet(StringComparer.Ordinal);
                int overlap=variantTokens.Count(t=>originalTokens.Contains(t));
                c.Score=20+(int)(15.0*overlap/Math.Max(1,originalTokens.Union(variantTokens).Count()));
            }
            result.Add(c);
        }
        return result.OrderByDescending(c=>c.Score).ThenByDescending(c=>c.Selectable).ThenBy(c=>c.Code,StringComparer.Ordinal).ThenBy(c=>c.Id).Take(5).ToList();
    }
    private static string Canonical(string code)=>string.IsNullOrWhiteSpace(code)?"":code.Contains(':')?code:"game:"+code;
}
