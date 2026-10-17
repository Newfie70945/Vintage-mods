using Vintagestory.API.Config;

namespace BlockLogger.Frontend;

/// <summary>
/// Client-side localization helper for the in-game Server Essentials GUI.
/// Vintage Story's Lang API automatically resolves against the player's selected game language.
/// Missing translations fall back through the normal Vintage Story language fallback behavior.
/// </summary>
internal static class SuiteLocalization
{
    private const string Domain = "newfiesserveressentialssuite";

    public static string Get(string key, params object[] args)
    {
        return Lang.Get($"{Domain}:{key}", args);
    }
}
