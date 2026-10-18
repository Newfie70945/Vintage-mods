using Vintagestory.API.Common;

namespace BlockLogger.WorldRepair;

// The official remapper gives missing assets a coded IsMissing placeholder.
// Null slots/code-less holes and registered code mismatches are diagnostic evidence only.
internal static class MissingBlockScan
{
    internal static bool IsPlaceholder(Block? block) => block?.IsMissing == true && block.Code != null;

    internal static bool Includes(int id, int? focus, bool diagnostics, bool[] placeholders, bool[] suspects)
    {
        if (id == 0 || (focus.HasValue && id != focus.Value)) return false;
        return diagnostics
            ? focus.HasValue || id < 0 || id >= suspects.Length || suspects[id]
            : id >= 0 && id < placeholders.Length && placeholders[id];
    }
}
