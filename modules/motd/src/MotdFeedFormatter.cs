using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace NewfiesCommunityMOTD;

// Formatting is separate from transport: owner VTML and unread IDs remain intact.
internal static class MotdFeedFormatter
{
    internal static string Normalize(string? text) => Regex.Replace(
        Regex.Replace((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n'), @"\[(?i:br)\]", "\n"), @"[ \t]+\n", "\n");
    // VTML decodes &lt;/&gt; but shows &amp;/&#39; literally, so HtmlEncode would garble
    // ordinary titles such as "We're live". Quotes are escaped only for attribute values.
    private static string Encode(string? text) => (text ?? "").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string EncodeAttribute(string text) => Encode(text).Replace("\"", "%22");
    internal static string Build(string heading, string? local, bool useRemote,
        IReadOnlyList<MotdFeedItem>? remote, int limit, string? accent, bool merge)
    {
        accent = Regex.IsMatch(accent ?? "", @"^#[0-9a-fA-F]{6}$") ? accent : "#D9B777";
        var items = useRemote && remote != null ? remote.Where(x => x != null).Take(Math.Clamp(limit, 1, 50)).ToArray() : Array.Empty<MotdFeedItem>();
        var sb = new StringBuilder();
        string intro = heading == "Updates" ? "Changes, fixes and improvements to your server." : heading == "Events" ? "Upcoming activities and community events." : "The latest news from your server community.";
        sb.Append("<font size=\"21\" color=\"").Append(accent).Append("\"><strong>").Append(Encode(heading)).Append("</strong></font>\n")
          .Append("<font size=\"12\" color=\"#AAB7BE\">").Append(intro).Append("</font>\n\n");
        bool hasLocal = !string.IsNullOrWhiteSpace(local) && (merge || items.Length == 0);
        if (hasLocal)
        {
            // Local posts already carry titles from the Suite editor; avoid another generic title.
            sb.Append(Normalize(local).Trim()).Append("\n\n");
        }
        for (int i = 0; i < items.Length; i++)
        {
            if (hasLocal || i > 0) sb.Append("<font size=\"11\" color=\"#52616C\">────────────────────────</font>\n\n");
            AppendCard(sb, items[i], accent!);
        }
        if (!hasLocal && items.Length == 0)
            sb.Append("<font color=\"#AAB7BE\"><i>").Append(heading == "Updates" ? "No updates have been posted yet." : heading == "Events" ? "No events have been posted yet." : "No announcements have been posted yet.").Append("</i></font>");
        return sb.ToString();
    }
    private static void AppendCard(StringBuilder sb, MotdFeedItem item, string accent)
    {
        // Keep this marker compatible with the existing per-player NEW/read-state decorator.
        if (!string.IsNullOrWhiteSpace(item.Id)) sb.Append("[[MOTDNEW:").Append(Encode(item.Id)).Append("]]");
        if (item.Important) sb.Append("<font size=\"11\" color=\"#EDBD78\"><strong>IMPORTANT</strong></font>\n");
        sb.Append("<font size=\"17\" color=\"").Append(accent).Append("\"><strong>").Append(Encode(string.IsNullOrWhiteSpace(item.Title) ? "Community post" : item.Title)).Append("</strong></font>\n");
        sb.Append("<font size=\"11\" color=\"#99ABB6\">").Append(Encode(string.IsNullOrWhiteSpace(item.Author) ? "Discord" : item.Author));
        if (item.UnixTime > 0)
        {
            try { sb.Append(" · ").Append(DateTimeOffset.FromUnixTimeSeconds(item.UnixTime).ToString("dd MMM yyyy · HH:mm 'UTC'", CultureInfo.InvariantCulture)); }
            catch (ArgumentOutOfRangeException) { /* Invalid optional dates do not hide a post. */ }
        }
        sb.Append("</font>\n\n");
        if (!string.IsNullOrWhiteSpace(item.BodyVtml)) sb.Append(Normalize(item.BodyVtml).Trim()).Append("\n\n");
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
            sb.Append("<font size=\"11\" color=\"#91B7C7\"><a href=\"").Append(EncodeAttribute(url.AbsoluteUri)).Append("\">Open original post</a></font>\n");
        sb.Append('\n');
    }
}
