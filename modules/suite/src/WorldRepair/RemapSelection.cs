namespace BlockLogger.WorldRepair;

public sealed record RemapChoice(int SourceId,string Destination);

internal static class RemapSelection
{
    internal const int Limit=32;
    internal static Dictionary<int,int> Resolve(IReadOnlyList<RemapChoice> choices,Func<string,int> resolve,int limit=Limit)
    {
        if(choices==null||(choices.Count<1||choices.Count>limit)||limit is <1 or >4096)throw new ArgumentException("Selection exceeds the supported mapping limit.");
        var result=new Dictionary<int,int>();
        foreach(var choice in choices)
        {
            if(choice==null||choice.SourceId==0||string.IsNullOrWhiteSpace(choice.Destination)||choice.Destination.Length>256)throw new ArgumentException("Each selected non-air ID needs a destination ID or exact block code.");
            int target=resolve(choice.Destination.Trim());
            if(target<0||target==choice.SourceId||!result.TryAdd(choice.SourceId,target))throw new ArgumentException("Sources must be distinct and destinations must be different valid IDs.");
        }
        return result;
    }
}
