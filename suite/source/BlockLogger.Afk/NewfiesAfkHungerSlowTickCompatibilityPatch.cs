namespace BlockLogger.Afk;

public static class NewfiesAfkHungerSlowTickCompatibilityPatch
{
	public static bool Prefix(object __instance)
	{
		NewfiesAfkModSystem instance = NewfiesAfkModSystem.Instance;
		if (instance == null)
		{
			return true;
		}
		return !instance.ShouldPauseHungerForAfk(__instance);
	}
}
