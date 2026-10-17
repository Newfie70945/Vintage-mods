using System.Reflection;
using Vintagestory.API.Common.Entities;

namespace BlockLogger.AntiCheat;

internal static class EntityRuntimeCompat
{
	private static readonly PropertyInfo? PosProperty = typeof(Entity).GetProperty("Pos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly FieldInfo? PosField = typeof(Entity).GetField("Pos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly PropertyInfo? ServerPosProperty = typeof(Entity).GetProperty("ServerPos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly FieldInfo? ServerPosField = typeof(Entity).GetField("ServerPos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly PropertyInfo? SidedPosProperty = typeof(Entity).GetProperty("SidedPos", BindingFlags.Instance | BindingFlags.Public);

	private static readonly FieldInfo? SidedPosField = typeof(Entity).GetField("SidedPos", BindingFlags.Instance | BindingFlags.Public);

	public static bool CanResolvePosition => PosProperty != null || PosField != null || ServerPosProperty != null || ServerPosField != null || SidedPosProperty != null || SidedPosField != null;

	public static EntityPos? GetPos(Entity? entity)
	{
		if (entity == null)
		{
			return null;
		}
		try
		{
			object obj = PosProperty?.GetValue(entity) ?? PosField?.GetValue(entity) ?? ServerPosProperty?.GetValue(entity) ?? ServerPosField?.GetValue(entity) ?? SidedPosProperty?.GetValue(entity) ?? SidedPosField?.GetValue(entity);
			return obj as EntityPos;
		}
		catch
		{
			return null;
		}
	}

	public static EntityPos? GetServerPos(Entity? entity)
	{
		if (entity == null)
		{
			return null;
		}
		try
		{
			object obj = ServerPosProperty?.GetValue(entity) ?? ServerPosField?.GetValue(entity) ?? PosProperty?.GetValue(entity) ?? PosField?.GetValue(entity) ?? SidedPosProperty?.GetValue(entity) ?? SidedPosField?.GetValue(entity);
			return obj as EntityPos;
		}
		catch
		{
			return null;
		}
	}
}
