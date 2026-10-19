namespace BlockLogger.WorldRepair;

internal static class RemapTargetPolicy
{
    internal sealed record Source(int Id,bool Missing,long Solid,long Fluid,long Decor);
    internal static string Issue(Source source,BlockRemapAdvisor.Target? target,bool allowAir,bool allowValid)
    {
        if(source.Id==0)return "Air cannot be a source ID.";
        if(target==null||target.Id<0)return "Destination is missing or unregistered.";
        if(source.Id==target.Id)return "Choose a different destination; source and destination IDs are identical.";
        if(!source.Missing)return "Source is registered and protected. Global recovery only changes missing definitions.";
        if(target.Id==0)return allowAir?"":"Replacing with air requires the explicit Allow Air option.";
        if(!string.IsNullOrEmpty(target.EntityClass))return "Destination requires block-entity/inventory data. Only exact original-code migration can reconnect compatible retained metadata.";
        if((source.Fluid>0&&!target.Fluid)||((source.Solid>0||source.Decor>0)&&target.Fluid))return "Destination does not match the affected solid/fluid layer.";
        return "";
    }
}
