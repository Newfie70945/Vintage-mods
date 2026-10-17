using System;
using System.Collections.Generic;
using System.Linq;
namespace BlockLogger.Frontend;

// Values are account UIDs. Names supplied by other mods are presentation only.
internal sealed class AdminPlayerChoices
{
    internal static readonly AdminPlayerChoices Empty = new(new[] { "" }, new[] { "No players online" });
    internal string[] Values { get; }
    internal string[] Labels { get; }
    private AdminPlayerChoices(string[] values, string[] labels) { Values = values; Labels = labels; }
    internal static AdminPlayerChoices Create(IEnumerable<(string Uid, string Name)> players)
    {
        var rows = players.Where(p => !string.IsNullOrWhiteSpace(p.Uid))
            .DistinctBy(p => p.Uid, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Uid, StringComparer.Ordinal).ToArray();
        if (rows.Length == 0) return Empty;
        var duplicateNames = rows.GroupBy(p => p.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(rows.Select(p => p.Uid).ToArray(), rows.Select(p =>
            string.IsNullOrWhiteSpace(p.Name) ? p.Uid : duplicateNames.Contains(p.Name) ? p.Name + " [" + p.Uid + "]" : p.Name).ToArray());
    }
}
