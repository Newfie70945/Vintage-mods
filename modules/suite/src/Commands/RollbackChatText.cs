using System.Text;

namespace BlockLogger.Commands;

// Rollback diagnostics are plain text, never authored VTML. A malformed line is
// retained in client chat history and can break each subsequent GUI recomposition.
internal static class RollbackChatText
{
    internal const int MaximumLength = 1600;
    internal static string Encode(string? text)
    {
        var result = new StringBuilder();
        const string suffix = "… (details in recovery report)";
        text ??= "";
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string value = c switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;",
                '\r' or '\n' or '\t' => " ",
                _ => char.IsControl(c) ? "" : c.ToString()
            };
            // Do not cut an entity or UTF-16 surrogate pair at the size boundary.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) value = text.Substring(i++, 2);
                else value = "�";
            }
            else if (char.IsLowSurrogate(c)) value = "�";
            if (result.Length + value.Length > MaximumLength - suffix.Length)
            { result.Append(suffix); break; }
            result.Append(value);
        }
        return result.ToString();
    }
}
