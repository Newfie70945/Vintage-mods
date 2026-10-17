namespace BlockLogger.Afk;

public static class NewfiesAfkHungerReductionCompatibilityPatch
{
	public static bool Prefix(object __instance, ref bool __result)
	{
		NewfiesAfkModSystem instance = NewfiesAfkModSystem.Instance;
		if (instance == null || !instance.ShouldPauseHungerForAfk(__instance))
		{
			return true;
		}
		__result = false;
		return false;
	}
}
