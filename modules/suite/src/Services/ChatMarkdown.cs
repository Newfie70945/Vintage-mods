using System;
using System.Text;

namespace BlockLogger.Services;

/// <summary>A bounded inline Markdown subset producing only stock-client VTML tags.</summary>
public static class ChatMarkdown
{
    public sealed record Result(string Vtml, string ModerationText);

    public static Result Format(string text, bool colors = false, bool links = false)
    {
        // Oversized messages retain their text; never amplify an unbounded player input.
        if (text.Length > 4096) return new(Escape(text), text);
        var parser = new Parser(text, colors, links);
        string vtml = parser.Read(0, text.Length, 0);
        return new(vtml, parser.Plain.ToString());
    }

    private static string Escape(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");

    private sealed class Parser(string text, bool colors, bool links)
    {
        public readonly StringBuilder Plain = new();
        private int spans;
        // Shared across recursive reads: malformed messages cannot trigger quadratic scans.
        private int scanBudget = 65536;

        public string Read(int start, int end, int depth)
        {
            var output = new StringBuilder();
            for (int i = start; i < end;)
            {
                if (text[i] == '\\' && i + 1 < end && "\\*_`[]()".Contains(text[i + 1]))
                {
                    Literal(output, text[i + 1]); i += 2; continue;
                }
                if (depth < 8 && spans < 64)
                {
                    if (text[i] == '`')
                    {
                        int close = FindCode(i + 1, end);
                        if (close > i + 1)
                        {
                            string code = text.Substring(i + 1, close - i - 1);
                            output.Append("<code>").Append(Escape(code)).Append("</code>");
                            Plain.Append(code); spans++; i = close + 1; continue;
                        }
                    }
                    if (colors && IsColorOpen(i, end))
                    {
                        string hex = text.Substring(i + 8, 6);
                        if (text[i + 14] == ']' && IsHex(hex))
                        {
                            int close = FindColorClose(i + 15, end);
                            if (close > i + 15)
                            {
                                spans++;
                                output.Append("<font color=\"#").Append(hex).Append("\">")
                                    .Append(Read(i + 15, close, depth + 1)).Append("</font>");
                                i = close + 8; continue;
                            }
                        }
                    }
                    if (links && text[i] == '[')
                    {
                        int labelEnd = FindLabelEnd(i + 1, end);
                        int urlEnd = labelEnd < 0 ? -1 : FindUrlEnd(labelEnd + 2, end);
                        if (labelEnd > i + 1 && urlEnd > labelEnd + 2)
                        {
                            string url = text.Substring(labelEnd + 2, urlEnd - labelEnd - 2);
                            if (SafeUrl(url))
                            {
                                string label = UnescapeLabel(i + 1, labelEnd);
                                output.Append("<a href=\"").Append(url).Append("\">").Append(Escape(label)).Append("</a>");
                                // Moderate both the visible label and destination, including hidden URLs.
                                Plain.Append(label).Append(" (").Append(url).Append(')');
                                spans++; i = urlEnd + 1; continue;
                            }
                        }
                    }
                    if (text[i] is '*' or '_')
                    {
                        char marker = text[i];
                        int run = 1;
                        while (i + run < end && text[i + run] == marker) run++;
                        // Underscores inside names/identifiers are ordinary text.
                        bool wordBoundary = (i == start || text[i - 1] != marker) &&
                            (marker != '_' || i == start || !char.IsLetterOrDigit(text[i - 1]));
                        if (run <= 3 && wordBoundary && i + run < end && !char.IsWhiteSpace(text[i + run]))
                        {
                            int close = FindEmphasisClose(marker, run, i + run, end);
                            if (close > i + run && !char.IsWhiteSpace(text[close - 1]) &&
                                (marker != '_' || close + run == end || !char.IsLetterOrDigit(text[close + run])))
                            {
                                spans++;
                                string body = Read(i + run, close, depth + 1);
                                output.Append(run == 3 ? "<strong><i>" : run == 2 ? "<strong>" : "<i>")
                                    .Append(body).Append(run == 3 ? "</i></strong>" : run == 2 ? "</strong>" : "</i>");
                                i = close + run; continue;
                            }
                        }
                        // Preserve unmatched marker runs rather than styling part of a run.
                        Literal(output, text.Substring(i, run)); i += run; continue;
                    }
                }
                Literal(output, text[i]); i++;
            }
            return output.ToString();
        }

        private void Literal(StringBuilder output, char value)
        {
            if (value == '<') output.Append("&lt;");
            else if (value == '>') output.Append("&gt;");
            else output.Append(value);
            Plain.Append(value);
        }

        private void Literal(StringBuilder output, string value) { output.Append(Escape(value)); Plain.Append(value); }

        private bool IsColorOpen(int index, int end) => index + 15 <= end &&
            text.AsSpan(index, 8).SequenceEqual("[color=#") && text[index + 14] == ']' &&
            IsHex(text.AsSpan(index + 8, 6));

        private int FindCode(int start, int end)
        {
            // Backslashes inside code are literal, including immediately before the closer.
            for (int i = start; i < end && scanBudget-- > 0; i++)
                if (text[i] == '`') return i;
            return -1;
        }

        private int SkipCode(int index, int end)
        {
            if (text[index] != '`') return index;
            int close = FindCode(index + 1, end);
            return close < 0 ? index : close;
        }

        private int FindColorClose(int start, int end)
        {
            int nesting = 1;
            for (int i = start; i < end && scanBudget-- > 0; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                int codeEnd = SkipCode(i, end);
                if (codeEnd != i) { i = codeEnd; continue; }
                if (links && text[i] == '[')
                {
                    int labelEnd = FindLabelEnd(i + 1, end);
                    int urlEnd = labelEnd < 0 ? -1 : FindUrlEnd(labelEnd + 2, end);
                    if (urlEnd >= 0 && SafeUrl(text.Substring(labelEnd + 2, urlEnd - labelEnd - 2)))
                    { i = urlEnd; continue; }
                }
                if (IsColorOpen(i, end)) { nesting++; i += 14; continue; }
                if (i + 8 <= end && text.AsSpan(i, 8).SequenceEqual("[/color]"))
                {
                    if (--nesting == 0) return i;
                    i += 7;
                }
            }
            return -1;
        }

        private int FindLabelEnd(int start, int end)
        {
            int brackets = 0;
            for (int i = start; i < end && scanBudget-- > 0; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '[') brackets++;
                else if (text[i] == ']')
                {
                    if (brackets > 0) brackets--;
                    else return i + 1 < end && text[i + 1] == '(' ? i : -1;
                }
            }
            return -1;
        }

        private int FindUrlEnd(int start, int end)
        {
            int parentheses = 0;
            for (int i = start; i < end && scanBudget-- > 0; i++)
            {
                if (text[i] == '(') parentheses++;
                else if (text[i] == ')')
                {
                    if (parentheses == 0) return i;
                    parentheses--;
                }
                // No scanning beyond the URL length limit (plus its closing parenthesis).
                if (i - start >= 512) return -1;
            }
            return -1;
        }

        private string UnescapeLabel(int start, int end)
        {
            var label = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (text[i] == '\\' && i + 1 < end && "\\*_`[]()".Contains(text[i + 1])) i++;
                label.Append(text[i]);
            }
            return label.ToString();
        }

        private int FindEmphasisClose(char marker, int length, int start, int end)
        {
            var inner = new System.Collections.Generic.Stack<int>();
            for (int i = start; i < end && scanBudget-- > 0; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                int codeEnd = SkipCode(i, end);
                if (codeEnd != i) { i = codeEnd; continue; }
                // Formatting markers in complete links/color spans belong to those spans.
                if (colors && IsColorOpen(i, end))
                {
                    int close = FindColorClose(i + 15, end);
                    if (close >= 0) { i = close + 7; continue; }
                }
                if (links && text[i] == '[')
                {
                    int labelEnd = FindLabelEnd(i + 1, end);
                    int urlEnd = labelEnd < 0 ? -1 : FindUrlEnd(labelEnd + 2, end);
                    if (urlEnd >= 0 && SafeUrl(text.Substring(labelEnd + 2, urlEnd - labelEnd - 2)))
                    { i = urlEnd; continue; }
                }
                if (text[i] != marker || (i > start && text[i - 1] == marker)) continue;
                int run = 1;
                while (i + run < end && text[i + run] == marker) run++;
                bool canClose = i > start && !char.IsWhiteSpace(text[i - 1]) &&
                    (marker != '_' || i + run == end || !char.IsLetterOrDigit(text[i + run]));
                bool canOpen = i + run < end && !char.IsWhiteSpace(text[i + run]) &&
                    (marker != '_' || i == start || !char.IsLetterOrDigit(text[i - 1]));
                if (run <= 3)
                {
                    int remaining = run;
                    if (canClose)
                    {
                        // An unmatched inner opener must not invalidate a complete outer span.
                        if (run == length) return i;
                        while (inner.Count > 0 && remaining >= inner.Peek()) remaining -= inner.Pop();
                        // Rightmost markers close the outer span after closing any inner styles.
                        if (inner.Count == 0 && remaining == length) return i + run - length;
                    }
                    if (canOpen && remaining > 0 && inner.Count < 8) inner.Push(remaining);
                }
                i += run - 1;
            }
            return -1;
        }

        private static bool IsHex(ReadOnlySpan<char> value)
        {
            foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static bool SafeUrl(string url)
        {
            if (url.Length > 512) return false;
            foreach (char c in url) if (char.IsWhiteSpace(c) || char.IsControl(c) || "\"'<>\\".Contains(c)) return false;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "https" || uri.Scheme == "http") && uri.UserInfo.Length == 0 && !string.IsNullOrEmpty(uri.Host);
        }
    }
}
