using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using NewfiesOptimizer.Optimizations;
using Vintagestory.GameContent;

internal static class PublishedWalkableFixture
{
    public static void Run(bool collision, Action checks)
    {
        var harmony=Install(collision);
        try{checks();}finally{harmony?.UnpatchAll(harmony.Id);}
    }
    public static Harmony? Install(bool collision)
    {
        string? root=Environment.GetEnvironmentVariable("NOS_COMPAT_FIXTURES");
        if(string.IsNullOrEmpty(root))return null;
        var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root,"walkableentities","WalkableEntities.dll"));
        var type=assembly.GetType("WalkableEntities.MovingFrame",true)!;
        var target=collision?AccessTools.Method(typeof(Vintagestory.API.MathTools.CollisionTester),"ApplyTerrainCollision"):AccessTools.Method(typeof(EntityBehaviorRepulseAgents),"WalkEntity");
        var handler=AccessTools.Method(type,collision?"Terrain":"Repulse");
        var harmony=new Harmony("walkableentities.runtime");
        if(collision)harmony.Patch(target,postfix:new HarmonyMethod(handler));else harmony.Patch(target,prefix:new HarmonyMethod(handler));
        var info=Harmony.GetPatchInfo(target)!;
        var patch=(collision?info.Postfixes:info.Prefixes).Single(p=>p.owner==harmony.Id);
        if(!KnownPatchCompatibility.Allows(collision?"collisionTraversalReuse":"repulseDelegateReuse",target,patch,collision?"postfix":"prefix"))
            throw new Exception("Unverified Walkable Entities fixture binary");
        Console.WriteLine("Published Walkable Entities 1.0.44 hook installed for native "+(collision?"collision":"repulsion")+" comparisons.");
        return harmony;
    }
}
