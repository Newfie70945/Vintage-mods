using System;
using System.Text.RegularExpressions;

namespace BlockLogger.WebDashboard;

// Bounded parsing/counters; no per-occurrence timestamp list or historical rate inference.
internal static class LogIssueEvidence
{
    private static readonly Regex Frames=new(@"^\s*at\s+([^\r\n(]+)",RegexOptions.Compiled|RegexOptions.Multiline|RegexOptions.CultureInvariant);
    private static readonly Regex PatchSuffix=new(@"_Patch\d+\b",RegexOptions.Compiled|RegexOptions.CultureInvariant);
    public static string Origin(string context)
    {
        foreach(Match match in Frames.Matches(context))
        {
            string value=match.Groups[1].Value.Trim();
            if(value.StartsWith("System.",StringComparison.Ordinal)||value.StartsWith("Microsoft.",StringComparison.Ordinal)||value.StartsWith("HarmonyLib.",StringComparison.Ordinal))continue;
            value=PatchSuffix.Replace(value,"_Patch");return value.Length>320?value[..320]:value;
        }
        return "";
    }
    public static bool IsNativeBuildLogger(string origin)=>origin.StartsWith("VintagestoryLib.Server.Systems.ServerSystemBlockLogger.",StringComparison.Ordinal);
    public static string WithOrigin(string signature,string origin)=>origin.Length==0?signature:signature+"|origin:"+origin.ToLowerInvariant();
}
internal sealed class RecentLogIssueCounts
{
    private const int Buckets=60;private const long BucketMs=5000;
    private readonly long[] stamps=new long[Buckets];private readonly int[] counts=new int[Buckets];
    public void Observe(long timestamp)
    {
        long slot=timestamp/BucketMs;int index=(int)(slot%Buckets);if(index<0)return;
        if(stamps[index]!=slot){stamps[index]=slot;counts[index]=0;}
        if(counts[index]<int.MaxValue)counts[index]++;
    }
    public int Count(long now)
    {
        long slot=now/BucketMs,total=0;
        for(int i=0;i<Buckets;i++)if(stamps[i]<=slot&&stamps[i]>slot-Buckets)total+=counts[i];
        return (int)Math.Min(total,int.MaxValue);
    }
}
