using System;
using System.Net;
using System.Text;

namespace Newfies.ChatProtocol;

// Compiled independently into each companion: no shared DLL or server/client dependency.
internal static class ChatMessageBoundary
{
    internal sealed record Line(string Body,bool HasSender)
    {
        public bool IsCommand => Body.TrimStart().StartsWith("/",StringComparison.Ordinal);
    }
    public static Line Parse(string? message,string? realName,string? displayName=null,string? roleName=null)
    {
        var raw=(message??"").TrimStart();
        if(raw.Length==0 || raw[0]=='/')return new(raw,false);
        var visible=new StringBuilder();int i=0;
        // Inspect only a bounded, trusted sender prefix. Never strip arbitrary markup from the authored body.
        while(i<raw.Length && i<512)
        {
            if(raw[i]=='<')
            {
                int end=raw.IndexOf('>',i+1);if(end<0 || end>=512)return new(raw,false);
                var tag=raw[(i+1)..end].Trim();
                if(!SenderTag(tag))return new(raw,false);
                i=end+1;continue;
            }
            if(raw[i]==':')
            {
                var sender=WebUtility.HtmlDecode(visible.ToString()).Trim();
                if(!Match(sender,realName,roleName) && !Match(sender,displayName,roleName))return new(raw,false);
                i++;
                // A colon may sit inside the bold/color wrapper or immediately after it.
                while(i<raw.Length)
                {
                    if(char.IsWhiteSpace(raw[i])){i++;continue;}
                    if(raw[i]=='<' && i+1<raw.Length && raw[i+1]=='/')
                    {
                        int end=raw.IndexOf('>',i+2);if(end<0 || !SenderTag(raw[(i+1)..end].Trim()))break;
                        i=end+1;continue;
                    }
                    break;
                }
                return new(raw[i..],true);
            }
            visible.Append(raw[i++]);
        }
        return new(raw,false);
    }
    private static bool Match(string value,string? name,string? role)
    {
        if(string.IsNullOrWhiteSpace(name))return false;
        if(value.Equals(name.Trim(),StringComparison.OrdinalIgnoreCase))return true;
        return !string.IsNullOrWhiteSpace(role) && value.Equals("["+role.Trim()+"] "+name.Trim(),StringComparison.OrdinalIgnoreCase);
    }
    private static bool SenderTag(string tag)
    {
        if(tag.StartsWith('/'))tag=tag[1..].TrimStart();
        int end=tag.IndexOfAny(new[]{' ','\t','\r','\n'});string name=end<0?tag:tag[..end];
        return name.Equals("strong",StringComparison.OrdinalIgnoreCase)||name.Equals("font",StringComparison.OrdinalIgnoreCase)||name.Equals("b",StringComparison.OrdinalIgnoreCase);
    }
}
