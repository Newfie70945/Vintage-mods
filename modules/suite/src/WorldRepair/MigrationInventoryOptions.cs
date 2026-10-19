using System.Globalization;

namespace BlockLogger.WorldRepair;

public sealed record MigrationInventoryOptions(string Mode="preserve",DateTime? BeforeUtc=null,bool RiskAcknowledged=false,bool OverwriteAcknowledged=false)
{
    public static MigrationInventoryOptions Parse(string mode,string before,bool risk,bool overwrite)
    {
        DateTime? cutoff=null;
        if(!string.IsNullOrWhiteSpace(before))
        {
            if(!DateTimeOffset.TryParse(before,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed))throw new ArgumentException("Inventory cutoff must be an ISO-8601 UTC date/time.");
            cutoff=parsed.UtcDateTime;
        }
        var result=new MigrationInventoryOptions(mode,cutoff,risk,overwrite);result.Validate();return result;
    }
    public void Validate()
    {
        if(Mode is not ("preserve" or "recover-empty" or "replace"))throw new ArgumentException("Choose preserve, recover-empty or replace inventory mode.");
        if(BeforeUtc.HasValue&&(BeforeUtc.Value.Kind!=DateTimeKind.Utc||BeforeUtc.Value>DateTime.UtcNow))throw new ArgumentException("Inventory cutoff must be UTC and cannot be in the future.");
        if(Mode!="preserve"&&!RiskAcknowledged)throw new InvalidOperationException("Acknowledge historical inventory recovery risks: snapshots may lack attributes and unlogged transfers can duplicate items.");
        if(Mode=="replace"&&!OverwriteAcknowledged)throw new InvalidOperationException("Explicitly acknowledge replacing existing container contents before using Replace mode.");
    }
}
public sealed class MigrationInventoryReport
{
    public string State {get;set;}="not-requested";public string Message {get;set;}="";
    public long Chunks {get;set;}public long Prepared {get;set;}public long Preserved {get;set;}public long Recovered {get;set;}public long Items {get;set;}public long Skipped {get;set;}
    public List<string> Samples {get;set;}=[];
}
