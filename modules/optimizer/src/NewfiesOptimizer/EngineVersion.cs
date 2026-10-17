using System.Reflection;
using Vintagestory.API.Config;

namespace NewfiesOptimizer;

internal static class EngineVersion
{
    // ShortGameVersion is const: direct C# references inline the BUILD version and cannot guard the running server.
    internal static string Actual => typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion), BindingFlags.Static | BindingFlags.Public)?.GetRawConstantValue() as string ?? "unknown";
}
