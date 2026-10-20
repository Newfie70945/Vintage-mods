using System.Net;
using System.Text.RegularExpressions;

namespace NewfiesServerEssentials.Discord.Relay;

internal static class GameEventText
{
    private static readonly Regex Markup=new("</?(?:font|strong|em|b|i|a|br)(?:\\s[^>]*)?/?>",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));
    private static readonly Regex Tokens=new("\\{(player|message|cause|source|killer|phase|strength)\\}",RegexOptions.None,TimeSpan.FromMilliseconds(50));
    internal static string Plain(string text)=>WebUtility.HtmlDecode(Markup.Replace(text.Length>4096?text[..4096]:text,""));
    internal static string Format(string format,IReadOnlyDictionary<string,string> values)
    {
        string Escape(string s)=>Plain(s).Replace("\\","\\\\").Replace("*","\\*").Replace("_","\\_").Replace("`","\\`").Replace("@everyone","@ everyone",StringComparison.OrdinalIgnoreCase).Replace("@here","@ here",StringComparison.OrdinalIgnoreCase).Replace('\r',' ').Replace('\n',' ');
        var result=Tokens.Replace(format.Length>1000?format[..1000]:format,m=>values.TryGetValue(m.Groups[1].Value,out var value)?Escape(value):m.Value);
        if(result.Length<=1800)return result;
        int end=1799;if(char.IsHighSurrogate(result[end-1]))end--;return result[..end]+"…";
    }
    internal static string Fallback(string player,string cause,string source,string killer)=>cause switch
    {
        "Hunger"=>$"{player} starved to death.",
        "Electricity"=>$"{player} was struck by lightning or electricity.",
        "Fire"=>$"{player} burned to death.",
        "Suffocation"=>$"{player} suffocated.",
        _ when killer.Length>0=>$"{player} was killed by {killer}.",
        _ when source=="Fall"=>$"{player} fell to their death.",
        _ when source=="Explosion"=>$"{player} died in an explosion.",
        _=>$"{player} died ({cause})."
    };
}

internal sealed record StormState(bool Active,int Notice,string Strength);
internal sealed class StormTransitions
{
    private StormState? previous;
    internal void Reset()=>previous=null;
    internal IReadOnlyList<(string Phase,string Strength)> Observe(StormState current)
    {
        var old=previous;previous=current;if(old==null)return [];
        var events=new List<(string,string)>();
        if(old.Active&&!current.Active){events.Add(("ended",old.Strength));return events;}
        if(current.Notice==1&&old.Notice!=1)events.Add(("approaching",current.Strength));
        if(current.Notice==0&&old.Notice>0)events.Add(("imminent",current.Strength));
        if(!old.Active&&current.Active)events.Add(("started",current.Strength));
        if(current.Active&&current.Notice==-1&&old.Notice!=-1)events.Add(("waning",current.Strength));
        return events;
    }
}
