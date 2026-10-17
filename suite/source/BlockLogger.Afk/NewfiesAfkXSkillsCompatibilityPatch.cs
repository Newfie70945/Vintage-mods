namespace BlockLogger.Afk;

public static class NewfiesAfkXSkillsCompatibilityPatch
{
	public static bool Prefix(object __instance, float __0)
	{
		NewfiesAfkModSystem instance = NewfiesAfkModSystem.Instance;
		if (instance == null)
		{
			return true;
		}
		return !instance.ShouldSuppressXSkillsSurvivalExperience(__instance, __0);
	}
}
