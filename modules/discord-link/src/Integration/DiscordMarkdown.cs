using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NewfiesServerEssentials.Discord.Integration;

// Converts Discord message markdown into Vintage Story VTML for the Community MOTD feeds.
// VTML has no underline, strikethrough or monospace, so those degrade to readable colours
// instead of leaking raw Discord syntax into the in-game reader.
internal static class DiscordMarkdown
{
    // Mention kinds passed to the resolver: '@' user, '&' role, '#' channel.
    internal delegate string? MentionResolver(char kind, ulong id);

    private const string MentionColor = "#A5B4FC";
    private const string CodeColor = "#E6C07B";
    private const string MutedColor = "#8A9AA5";
    private const string QuoteColor = "#B8C4CB";
    private const char HoldOpen = '', HoldClose = '';

    private static readonly Regex Heading = new(@"^(#{1,3})\s+(.+)$");
    private static readonly Regex Subtext = new(@"^-#\s+(.+)$");
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(.+)$");
    private static readonly Regex Numbered = new(@"^(\s*)(\d{1,3})[.)]\s+(.+)$");
    private static readonly Regex Escaped = new(@"\\([\\*_~`|>#\-\[\]()<:])");
    private static readonly Regex InlineCode = new(@"(`{1,2})(?!`)(.+?)(?<!`)\1(?!`)");
    private static readonly Regex MaskedLink = new(@"\[([^\[\]\n]+)\]\(<?(https?://[^\s)<>]+)>?\)");
    private static readonly Regex Angle = new(@"<(@!?|@&|#)(\d{1,20})>|<a?:(\w{1,32}):\d{1,20}>|<t:(-?\d{1,13})(?::([tTdDfFR]))?>|<(https?://[^\s<>]+)>");
    private static readonly Regex BareUrl = new(@"https?://[^\s<>" + HoldOpen + HoldClose + @"]+[^\s<>.,;:!?)'""" + HoldOpen + HoldClose + @"]");
    private static readonly Regex Held = new(HoldOpen + @"(\d+)" + HoldClose);

    internal static string ToVtml(string? markdown, MentionResolver? resolve = null)
    {
        string text = StripEmoji((markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n')).Trim('\n');
        if (text.Length == 0) return "";

        var output = new StringBuilder();
        var lines = text.Split('\n');
        bool inFence = false, quoteRest = false;
        var fence = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                string after = line.TrimStart()[3..];
                if (!inFence)
                {
                    // ```lang opens a block; ```code``` on one line is a one-line block.
                    int close = after.IndexOf("```", StringComparison.Ordinal);
                    if (close >= 0) { AppendCodeLine(output, after[..close]); continue; }
                    inFence = true; fence.Clear();
                    if (after.Trim().Length > 0 && after.Trim().Contains(' ')) fence.Append(after).Append('\n');
                    continue;
                }
                inFence = false;
                foreach (var codeLine in fence.ToString().TrimEnd('\n').Split('\n')) AppendCodeLine(output, codeLine);
                continue;
            }
            if (inFence) { fence.Append(line).Append('\n'); continue; }

            bool quoted = quoteRest;
            if (!quoted && line.StartsWith(">>> ", StringComparison.Ordinal)) { quoteRest = quoted = true; line = line[4..]; }
            else if (!quoted && (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")) { quoted = true; line = line.Length > 1 ? line[2..] : ""; }

            string body = Block(line, resolve);
            if (quoted) output.Append("<font color=\"").Append(QuoteColor).Append("\">│ ").Append(body).Append("</font>\n");
            else output.Append(body).Append('\n');
        }
        // An unterminated fence still shows its content rather than swallowing the post.
        if (inFence) foreach (var codeLine in fence.ToString().TrimEnd('\n').Split('\n')) AppendCodeLine(output, codeLine);

        return Regex.Replace(output.ToString(), @"\n{3,}", "\n\n").TrimEnd('\n');
    }

    // Plain single-line text for titles: Discord syntax removed, nothing escaped.
    internal static string ToPlainText(string? markdown, MentionResolver? resolve = null)
    {
        string text = StripEmoji((markdown ?? "").Replace('\r', ' ').Replace('\n', ' ')).Trim();
        var held = new List<string>();
        text = Escaped.Replace(text, m => Hold(held, m.Groups[1].Value));
        text = InlineCode.Replace(text, m => Hold(held, m.Groups[2].Value));
        text = MaskedLink.Replace(text, m => Hold(held, m.Groups[1].Value));
        text = Angle.Replace(text, m => Hold(held, AngleText(m, resolve)));
        text = Regex.Replace(text, @"^(#{1,3}|-#|>>>|>)\s+", "");
        text = Regex.Replace(text, @"\*\*\*|\*\*|__|~~|\|\||(?<!\w)[*_](?=\S)|(?<=\S)[*_](?!\w)", "");
        return Restore(text, held).Trim();
    }

    // The game font has no emoji glyphs, so they draw as empty squares. Common status emoji
    // become plain symbols; every other emoji (with its modifiers and joiners) is removed.
    // Replacements avoid markdown characters (* + -) so they cannot start lists or italics.
    private static readonly Dictionary<int, string> EmojiText = new()
    {
        [0x2705] = "[+]", [0x2714] = "[+]", [0x2611] = "[+]",
        [0x274C] = "[x]", [0x2716] = "[x]", [0x274E] = "[x]",
        [0x26A0] = "[!]", [0x2757] = "!", [0x2755] = "!", [0x203C] = "!!",
        [0x2753] = "?", [0x2754] = "?",
        [0x27A1] = "->", [0x2B05] = "<-",
        [0x1F534] = "●", [0x1F7E0] = "●", [0x1F7E1] = "●", [0x1F7E2] = "●", [0x1F535] = "●", [0x1F7E3] = "●", [0x26AA] = "●", [0x26AB] = "●",
    };

    private static bool IsEmoji(int c) =>
        c is >= 0x1F000 and <= 0x1FAFF          // pictographs, emoticons, flags, skin tones
        or >= 0x2600 and <= 0x27BF              // misc symbols and dingbats
        or >= 0x2B00 and <= 0x2BFF              // arrows/stars such as ⭐ ⬆
        or >= 0xE0020 and <= 0xE007F            // flag tag sequences
        or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299
        or 0x231A or 0x231B or 0x2328 or 0x23CF or >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA
        or 0x24C2 or 0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE or 0x2934 or 0x2935;

    internal static string StripEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var output = new StringBuilder(text.Length);
        bool dropSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            // Joiners, variation selectors and keycap marks only exist to build emoji.
            if (c is 0x200D or 0xFE0E or 0xFE0F or 0x20E3) continue;
            if (EmojiText.TryGetValue(c, out var replacement)) { output.Append(replacement); dropSpace = false; continue; }
            if (IsEmoji(c))
            {
                // "🎉 Big news" should read "Big news", not " Big news".
                dropSpace = output.Length == 0 || output[^1] is ' ' or '\n' or '\t';
                continue;
            }
            if (dropSpace && c == ' ') { dropSpace = false; continue; }
            dropSpace = false;
            output.Append(rune.ToString());
        }
        return output.ToString();
    }

    private static void AppendCodeLine(StringBuilder output, string line) =>
        output.Append("<font color=\"").Append(CodeColor).Append("\">    ").Append(Escape(line.TrimEnd())).Append("</font>\n");

    private static string Block(string line, MentionResolver? resolve)
    {
        Match m;
        if ((m = Heading.Match(line)).Success)
        {
            string size = m.Groups[1].Length switch { 1 => "20", 2 => "18", _ => "17" };
            return "<font size=\"" + size + "\"><strong>" + Inline(m.Groups[2].Value, resolve) + "</strong></font>";
        }
        if ((m = Subtext.Match(line)).Success)
            return "<font size=\"12\" color=\"" + MutedColor + "\">" + Inline(m.Groups[1].Value, resolve) + "</font>";
        if ((m = Bullet.Match(line)).Success)
            return Indent(m.Groups[1].Value) + "• " + Inline(m.Groups[2].Value, resolve);
        if ((m = Numbered.Match(line)).Success)
            return Indent(m.Groups[1].Value) + m.Groups[2].Value + ". " + Inline(m.Groups[3].Value, resolve);
        return Inline(line, resolve);
    }

    // Discord nests lists by two spaces; VTML collapses leading spaces less aggressively with a bullet.
    private static string Indent(string leading) => new(' ', 2 + Math.Min(8, leading.Replace("\t", "  ").Length / 2 * 4));

    private static string Inline(string text, MentionResolver? resolve)
    {
        var held = new List<string>();
        text = Escaped.Replace(text, m => Hold(held, Escape(m.Groups[1].Value)));
        text = InlineCode.Replace(text, m => Hold(held, "<font color=\"" + CodeColor + "\">" + Escape(m.Groups[2].Value.Trim()) + "</font>"));
        text = MaskedLink.Replace(text, m => Hold(held, Link(m.Groups[2].Value, Escape(ToPlainText(m.Groups[1].Value, resolve)))));
        text = Angle.Replace(text, m => Hold(held, AngleVtml(m, resolve)));
        text = BareUrl.Replace(text, m => Hold(held, Link(m.Value, Escape(m.Value))));
        text = Escape(text);

        text = Regex.Replace(text, @"\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*", "<strong><i>$1</i></strong>");
        text = Regex.Replace(text, @"\*\*(?=\S)(.+?)(?<=\S)\*\*", "<strong>$1</strong>");
        text = Regex.Replace(text, @"__(?=\S)(.+?)(?<=\S)__", "<strong>$1</strong>");
        text = Regex.Replace(text, @"(?<![\w*])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![\w*])", "<i>$1</i>");
        text = Regex.Replace(text, @"(?<![\w_])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![\w_])", "<i>$1</i>");
        text = Regex.Replace(text, @"~~(?=\S)(.+?)(?<=\S)~~", "<font color=\"" + MutedColor + "\">$1</font>");
        text = Regex.Replace(text, @"\|\|(.+?)\|\|", "<font color=\"" + MutedColor + "\">$1</font>");
        return Restore(text, held);
    }

    private static string AngleVtml(Match m, MentionResolver? resolve)
    {
        if (m.Groups[6].Success) return Link(m.Groups[6].Value, Escape(m.Groups[6].Value));
        string label = Escape(AngleText(m, resolve));
        return m.Groups[1].Success ? "<font color=\"" + MentionColor + "\"><strong>" + label + "</strong></font>" : label;
    }

    private static string AngleText(Match m, MentionResolver? resolve)
    {
        if (m.Groups[1].Success)
        {
            char kind = m.Groups[1].Value switch { "@&" => '&', "#" => '#', _ => '@' };
            string? name = ulong.TryParse(m.Groups[2].Value, out ulong id) ? resolve?.Invoke(kind, id) : null;
            string fallback = kind switch { '&' => "role", '#' => "channel", _ => "user" };
            return (kind == '#' ? "#" : "@") + (string.IsNullOrWhiteSpace(name) ? fallback : name.Trim());
        }
        if (m.Groups[3].Success) return ":" + m.Groups[3].Value + ":";
        if (m.Groups[4].Success) return Timestamp(m.Groups[4].Value, m.Groups[5].Value);
        return m.Groups[6].Value;
    }

    private static string Timestamp(string unix, string style)
    {
        if (!long.TryParse(unix, out long seconds)) return unix;
        try
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            string format = style switch
            {
                "t" => "HH:mm 'UTC'",
                "T" => "HH:mm:ss 'UTC'",
                "d" or "D" => "dd MMM yyyy",
                _ => "dd MMM yyyy · HH:mm 'UTC'"
            };
            return time.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return unix; }
    }

    private static string Link(string url, string label) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? "<a href=\"" + Escape(uri.AbsoluteUri).Replace("\"", "%22") + "\">" + label + "</a>"
            : label;

    private static string Hold(List<string> held, string value)
    {
        held.Add(value);
        return HoldOpen + (held.Count - 1).ToString(CultureInfo.InvariantCulture) + HoldClose;
    }

    private static string Restore(string text, List<string> held)
    {
        // Held values can contain earlier holds (for example a link label), so unwind fully.
        for (int pass = 0; pass < 4 && text.IndexOf(HoldOpen) >= 0; pass++)
            text = Held.Replace(text, m => int.TryParse(m.Groups[1].Value, out int i) && i < held.Count ? held[i] : "");
        return text;
    }

    // VTML decodes &lt;/&gt; but shows &amp;/&#39; literally, so only angle brackets are escaped.
    internal static string Escape(string value) => value.Replace("<", "&lt;").Replace(">", "&gt;");
}
