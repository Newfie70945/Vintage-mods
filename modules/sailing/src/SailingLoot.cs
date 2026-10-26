using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace NewfiesSailing;

public static class SailingLoot
{
    /// <summary>Spawns each listed item at the position with its chance; unknown item codes are skipped. Returns the number spawned.</summary>
    public static int Spawn(IWorldAccessor world, IEnumerable<(string Code, int Min, int Max, double Chance)> table, Vec3d pos)
    {
        int found = 0;
        foreach (var (code, min, max, chance) in table)
        {
            if (world.Rand.NextDouble() > chance) continue;
            Item? item = world.GetItem(new AssetLocation(code));
            if (item == null) continue;
            world.SpawnItemEntity(new ItemStack(item, world.Rand.Next(min, max + 1)), pos);
            found++;
        }
        return found;
    }
}
