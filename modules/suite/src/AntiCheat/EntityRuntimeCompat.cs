using System.Reflection;
using Vintagestory.API.Common.Entities;

namespace BlockLogger.AntiCheat;

/// <summary>
/// Runtime-compatible access to entity position across Vintage Story API/reference builds.
/// Entity.Pos has existed as both a public field and a get-only property across reference sets.
/// Binding to it directly can produce a FieldNotFoundException/MethodNotFoundException when a
/// mod is compiled against a stale or mixed API set and then loaded by 1.22.7. Resolve by name
/// at runtime instead so PacketGuard cannot JIT-crash the server over that ABI difference.
/// </summary>
internal static class EntityRuntimeCompat
{
    private static readonly PropertyInfo? PosProperty =
        typeof(Entity).GetProperty("Pos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo? PosField =
        typeof(Entity).GetField("Pos", BindingFlags.Instance | BindingFlags.Public);

    private static readonly PropertyInfo? ServerPosProperty =
        typeof(Entity).GetProperty("ServerPos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo? ServerPosField =
        typeof(Entity).GetField("ServerPos", BindingFlags.Instance | BindingFlags.Public);

    private static readonly PropertyInfo? SidedPosProperty =
        typeof(Entity).GetProperty("SidedPos", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo? SidedPosField =
        typeof(Entity).GetField("SidedPos", BindingFlags.Instance | BindingFlags.Public);

    public static bool CanResolvePosition =>
        PosProperty != null || PosField != null || ServerPosProperty != null || ServerPosField != null || SidedPosProperty != null || SidedPosField != null;

    public static EntityPos? GetPos(Entity? entity)
    {
        if (entity == null) return null;
        try
        {
            object? value = PosProperty?.GetValue(entity)
                ?? PosField?.GetValue(entity)
                ?? ServerPosProperty?.GetValue(entity)
                ?? ServerPosField?.GetValue(entity)
                ?? SidedPosProperty?.GetValue(entity)
                ?? SidedPosField?.GetValue(entity);
            return value as EntityPos;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the server-authoritative entity position when available.  Ore Shield uses this
    /// for proximity reveal checks so walking toward an ore vein is not gated by a stale client/
    /// sided Pos snapshot.  The fallback order remains runtime-compatible with mixed API builds.
    /// </summary>
    public static EntityPos? GetServerPos(Entity? entity)
    {
        if (entity == null) return null;
        try
        {
            object? value = ServerPosProperty?.GetValue(entity)
                ?? ServerPosField?.GetValue(entity)
                ?? PosProperty?.GetValue(entity)
                ?? PosField?.GetValue(entity)
                ?? SidedPosProperty?.GetValue(entity)
                ?? SidedPosField?.GetValue(entity);
            return value as EntityPos;
        }
        catch
        {
            return null;
        }
    }
}
