using System.Globalization;

namespace BlockLogger.WorldRepair;

public sealed record MigrationInventoryOptions(string Mode="preserve",DateTime? BeforeUtc=null,bool RiskAcknowledged=false,bool OverwriteAcknowledged=false)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool RequiresPreparation=>Mode is "recover-empty" or "replace";
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
        if(Mode is not ("preserve" or "reconnect" or "recover-empty" or "replace"))throw new ArgumentException("Choose preserve, reconnect, recover-empty or replace inventory mode.");
        if(BeforeUtc.HasValue&&(BeforeUtc.Value.Kind!=DateTimeKind.Utc||BeforeUtc.Value>DateTime.UtcNow))throw new ArgumentException("Inventory cutoff must be UTC and cannot be in the future.");
        if(Mode!="preserve"&&!RiskAcknowledged)throw new InvalidOperationException(Mode=="reconnect"?"Add confirm-inventory-risk to this command to acknowledge container reconnection. Example: /nseblockmigrate replaceheld reconnect confirm-inventory-risk. Incompatible or missing metadata will remain unchanged.":"Add confirm-inventory-risk to this command. Example: /nseblockmigrate replaceheld recover-empty confirm-inventory-risk. Historical snapshots may lack item attributes, and unlogged transfers can duplicate items.");
        if(Mode=="replace"&&!OverwriteAcknowledged)throw new InvalidOperationException("Replace mode also requires confirm-overwrite because it replaces existing container contents.");
    }
}
public sealed class MigrationInventoryReport
{
    public string State {get;set;}="not-requested";public string Message {get;set;}="";
    public long Chunks {get;set;}public long Prepared {get;set;}public long Preserved {get;set;}public long Recovered {get;set;}public long Items {get;set;}public long Skipped {get;set;}
    public List<string> Samples {get;set;}=[];
}
