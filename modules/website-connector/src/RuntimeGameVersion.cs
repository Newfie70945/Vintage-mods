using System.Reflection;

namespace Newfies.WebsiteConnector;

public static class RuntimeGameVersion
{
    // Direct reads of GameVersion constants are inlined from the build machine's API.
    // Reflection reads the metadata in the API assembly loaded by this game server.
    public static string Read(Type gameVersionType)
    {
        foreach (var name in new[] { "ShortGameVersion", "OverallVersion" })
        {
            var field = gameVersionType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }
}
