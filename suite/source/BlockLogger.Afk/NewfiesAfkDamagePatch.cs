using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace BlockLogger.Afk;

public static class NewfiesAfkDamagePatch
{
	public static MethodBase? TargetMethod()
	{
		return typeof(Entity).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo method) => method.Name == "ReceiveDamage" && method.GetParameters().Length >= 2);
	}

	public static void Postfix(Entity __instance, object[] __args, bool __result)
	{
		if (__result && __instance is EntityPlayer entity)
		{
			float damage = 1f;
			if (__args.Length >= 2 && __args[1] is float num)
			{
				damage = num;
			}
			NewfiesAfkModSystem.Instance?.MarkDamaged(entity, damage);
		}
	}
}
