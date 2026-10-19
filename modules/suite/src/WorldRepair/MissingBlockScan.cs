using Vintagestory.API.Common;

namespace BlockLogger.WorldRepair;

// Missing definitions include code-less holes left by vanilla ID remapping.
// Healthy registered IDs are never included by the default recovery scan.
internal static class MissingBlockScan
{
    internal static bool IsPlaceholder(Block? block) => block == null || block.Code == null || block.IsMissing;

    internal static bool Includes(int id, int? focus, bool diagnostics, bool[] placeholders, bool[] suspects)
    {
        if (id == 0 || (focus.HasValue && id != focus.Value)) return false;
        return diagnostics
            ? focus.HasValue || id < 0 || id >= suspects.Length || suspects[id]
            : id < 0 || id >= placeholders.Length || placeholders[id];
    }
}
