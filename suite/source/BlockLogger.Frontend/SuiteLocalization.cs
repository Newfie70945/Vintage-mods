using Vintagestory.API.Config;

namespace BlockLogger.Frontend;

internal static class SuiteLocalization
{
	private const string Domain = "newfiesserveressentialssuite";

	public static string Get(string key, params object[] args)
	{
		return Lang.Get("newfiesserveressentialssuite:" + key, args);
	}
}
