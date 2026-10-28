using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;

namespace NewfiesOptimizer.Optimizations;

// Exceptions identify an audited target, patch kind, owner, handler AND binary.
// They do not grant a mod owner permission to patch other dependencies. Hashing
// happens once per loaded assembly; recurring compatibility checks do no file I/O.
public static class KnownPatchCompatibility
{
    public const string ArlOwner = "ARL (Ignore this, look at stacktrace instead! in 99.9% cases the crash is not related to ARL)";
    private sealed record Identity(string Hash);
    private static readonly ConditionalWeakTable<Assembly, Identity> identities = new();
    private const string Walkable = "1daaa4b1a69c270c7ccefd5aa4730820313f119d4584c67a2e312db4e8b425c6";
    private const string GeneLib = "29773f973b482186b55464d6819b9b8a136c7e2f57a8fb02c2d2db6dfe6ef6a2";
    private const string Suite = "7da05293c81202193b588e42c284ca26b0afa2003540fe7fdb57dd6153d161ca";
    private static readonly string[] Arl = [
        "8fa22db982ab466446e241a62d66f04be694d351ee2ee693f0dab494ed70a010", // 3.2.0
        "817ee5f024bbde2b8be218a65e368a3f3ce9acbe7494bf9678fbf762d79ee2b5", // 3.1.5
        "df96de712ce8bc543b077fb302ca13b1e07d7ced9a4503fd81b0c7393de25b00"  // 3.1.4
    ];

    public static bool Allows(string module, MethodBase target, Patch patch, string kind)
    {
        try
        {
            string? handlerType = null, handler = null, expectedHash = null;
            if (module == "assetCompressionCache" && kind == "postfix" && patch.owner == "sekelsta.genelib" &&
                target.DeclaringType?.FullName == "Vintagestory.Server.ServerMain" && target.Name == "SendServerAssets")
                (handlerType, handler, expectedHash) = ("Genelib.GenelibSystem", "SendServerAssets_Postfix", GeneLib);
            else if (module == "collisionTraversalReuse" && kind == "postfix" && patch.owner == "walkableentities.runtime" &&
                target.DeclaringType?.FullName == "Vintagestory.API.MathTools.CollisionTester" && target.Name == "ApplyTerrainCollision")
                (handlerType, handler, expectedHash) = ("WalkableEntities.MovingFrame", "Terrain", Walkable);
            else if (module == "repulseDelegateReuse" && kind == "prefix" && patch.owner == "walkableentities.runtime" &&
                target.DeclaringType?.FullName == "Vintagestory.GameContent.EntityBehaviorRepulseAgents" && target.Name == "WalkEntity")
                (handlerType, handler, expectedHash) = ("WalkableEntities.MovingFrame", "Repulse", Walkable);
            else if (module == "blockUpdateListReuse" && kind == "prefix" && patch.owner == "newfies.serveressentials.anticheat.oreshield" &&
                target.DeclaringType?.FullName == "Vintagestory.Server.ServerMain" && target.Name == "SendSetBlocksPacket")
                (handlerType, handler, expectedHash) = ("BlockLogger.AntiCheat.OreShieldService", "BeforeSetBlocksPacket", Suite);
            else if (module == "idleFirepitFastPath" && kind == "transpiler" &&
                (patch.owner == ArlOwner || patch.owner == "attributerenderinglibrary") &&
                target.DeclaringType?.FullName == "Vintagestory.GameContent.BlockEntityFirepit" &&
                target.Name is "canHeatInput" or "canHeatOutput" or "canSmeltInput" or "canSmelt" or "getCombustibleOpts")
                (handlerType, handler) = ("AttributeRenderingLibrary.HarmonyPatches.AttributeRedirectionPatch", "Transpiler");
            if (handlerType == null) return false;
            var method = patch.PatchMethod;
            if (method.DeclaringType?.FullName != handlerType || method.Name != handler) return false;
            string hash = identities.GetValue(method.Module.Assembly, Identify).Hash;
            return expectedHash != null ? hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) :
                Arl.Contains(hash, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; } // Unverifiable/unknown handlers retain the normal conflict gate.
    }

    private static Identity Identify(Assembly assembly)
    {
        try { using var file = File.OpenRead(assembly.Location); return new(Convert.ToHexString(SHA256.HashData(file))); }
        catch { return new(""); }
    }
}
