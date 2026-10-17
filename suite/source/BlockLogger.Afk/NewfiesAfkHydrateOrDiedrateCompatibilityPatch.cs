namespace BlockLogger.Afk;

public static class NewfiesAfkHydrateOrDiedrateCompatibilityPatch
{
	public static bool Prefix(object __instance)
	{
		NewfiesAfkModSystem instance = NewfiesAfkModSystem.Instance;
		if (instance == null)
		{
			return true;
		}
		return !instance.ShouldPauseHydrateOrDiedrateThirstForAfk(__instance);
	}
}
