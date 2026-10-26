using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// A derelict wreck adrift on the water, usually found in fog. It drifts slowly with the wind, can be salvaged once
/// by right-clicking for assorted loot, and disappears after a while or when no one is nearby.
/// </summary>
public sealed class EntityDriftingWreck : Entity
{
    public const string LogKey = "newfiessailing:log:wrecks";
    public const double LifeSeconds = 1200;
    private const string LootedKey = "newfiessailing:looted";

    // item code, min, max, chance
    private static readonly (string Code, int Min, int Max, double Chance)[] Loot =
    {
        ("game:gear-rusty", 1, 3, 0.9),
        ("game:flaxtwine", 2, 5, 0.8),
        ("game:ingot-copper", 1, 2, 0.5),
        ("game:plank-oak", 2, 4, 0.7),
        ("game:ingot-iron", 1, 1, 0.2),
        ("game:feather", 1, 3, 0.4)
    };

    private double age, baseY = double.NaN, checkTimer;

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (World.Side != EnumAppSide.Server) return;

        age += dt;
        if (double.IsNaN(baseY)) baseY = ServerPos.Y;
        checkTimer += dt;
        if (age > LifeSeconds) { Die(EnumDespawnReason.Expire); return; }
        if (checkTimer > 30)
        {
            checkTimer = 0;
            IPlayer? near = World.NearestPlayer(ServerPos.X, ServerPos.Y, ServerPos.Z);
            if (near?.Entity == null || near.Entity.Pos.DistanceTo(ServerPos.XYZ) > 200) { Die(EnumDespawnReason.Expire); return; }
        }

        // Drift with the wind, but only over open water.
        Vec3d wind = World.BlockAccessor.GetWindSpeedAt(ServerPos.XYZ) ?? new Vec3d();
        double nx = ServerPos.X + wind.X * dt * 0.6, nz = ServerPos.Z + wind.Z * dt * 0.6;
        if (World.BlockAccessor.GetBlock(new BlockPos((int)nx, (int)(baseY - 0.6), (int)nz)).IsLiquid()) { ServerPos.X = nx; ServerPos.Z = nz; }
        ServerPos.Y = baseY + 0.08 * Math.Sin(age * 0.9);
        Pos.SetFrom(ServerPos);
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        base.OnInteract(byEntity, itemslot, hitPosition, mode);
        if (World.Side != EnumAppSide.Server || mode != EnumInteractMode.Interact || WatchedAttributes.GetBool(LootedKey, false)) return;
        WatchedAttributes.SetBool(LootedKey, true);

        int found = 0;
        foreach (var (code, min, max, chance) in Loot)
        {
            if (World.Rand.NextDouble() > chance) continue;
            Item? item = World.GetItem(new AssetLocation(code));
            if (item == null) continue;
            World.SpawnItemEntity(new ItemStack(item, World.Rand.Next(min, max + 1)), ServerPos.XYZ.AddCopy(0, 1, 0));
            found++;
        }
        if (byEntity is EntityPlayer ep && ep.Player is IServerPlayer sp)
        {
            ep.WatchedAttributes.SetInt(LogKey, ep.WatchedAttributes.GetInt(LogKey, 0) + 1);
            sp.SendMessage(GlobalConstants.InfoLogChatGroup, found > 0
                ? "[Sailing] You pick through the rotting wreck and find a few useful scraps before it slips beneath the waves."
                : "[Sailing] The wreck is picked clean. It slips beneath the waves.", EnumChatType.Notification);
        }
        World.PlaySoundAt(new AssetLocation("newfiessailing:sounds/waveslam"), ServerPos.X, ServerPos.Y, ServerPos.Z, null, true, 32, 0.8f);
        Die(EnumDespawnReason.Removed);
    }
}
