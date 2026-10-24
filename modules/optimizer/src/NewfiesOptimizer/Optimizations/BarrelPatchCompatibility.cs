using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
namespace NewfiesOptimizer.Optimizations;

// This exception is deliberately narrower than a mod-ID allowlist. The audited 0.7.1
// postfix only changes successful tea/decoction matches to false. Harmony still runs
// that postfix when our prefix rejects an impossible count. Unknown builds stay blocked.
public static class BarrelPatchCompatibility
{
    private sealed record Verification(bool Matches);
    private static readonly ConditionalWeakTable<Assembly, Verification> verified = new();
    public static bool Allows(Patches? patches, string ownId)
    {
        if (patches == null) return true;
        if (patches.Prefixes.Concat(patches.Transpilers).Concat(patches.Finalizers).Any(p => p.owner != ownId)) return false;
        foreach (var patch in patches.Postfixes)
        {
            if (patch.owner == ownId) continue;
            var method = patch.PatchMethod;
            if (patch.owner != "steepandbrew.patches" || method.Name != "Postfix" ||
                method.DeclaringType?.FullName != "SteepAndBrew.JarOnlyBarrelRecipePatch" ||
                !verified.GetValue(method.Module.Assembly, Verify).Matches) return false;
        }
        return true;
    }
    private static Verification Verify(Assembly assembly)
    {
        try
        {
            using var file = File.OpenRead(assembly.Location);
            return new(Convert.ToHexString(SHA256.HashData(file)).Equals(
                "5a0cabf9bb7c049db28ac6cdecd7503e06967189ddc8fd1291ca1e926cd12e66", StringComparison.OrdinalIgnoreCase));
        }
        catch { return new(false); }
    }
}
