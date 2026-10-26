using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// Base for things that drift on the water with the wind (wrecks, bottles): bob at the surface, expire after a while or
/// when no one is near, and are salvaged once by right-click.
/// </summary>
public abstract class EntityDriftingThing : Entity
{
    private const string SalvagedKey = "newfiessailing:salvaged";
    private double age, baseY = double.NaN, checkTimer;

    protected abstract double LifeSeconds { get; }
    protected virtual double DriftSpeed => 0.6;

    /// <summary>Called once on the server when a player salvages the entity.</summary>
    protected abstract void Salvage(IServerPlayer player);

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
        double nx = ServerPos.X + wind.X * dt * DriftSpeed, nz = ServerPos.Z + wind.Z * dt * DriftSpeed;
        if (World.BlockAccessor.GetBlock(new BlockPos((int)nx, (int)(baseY - 0.6), (int)nz)).IsLiquid()) { ServerPos.X = nx; ServerPos.Z = nz; }
        ServerPos.Y = baseY + 0.08 * Math.Sin(age * 0.9);
        Pos.SetFrom(ServerPos);
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        base.OnInteract(byEntity, itemslot, hitPosition, mode);
        if (World.Side != EnumAppSide.Server || mode != EnumInteractMode.Interact || WatchedAttributes.GetBool(SalvagedKey, false)) return;
        if ((byEntity as EntityPlayer)?.Player is not IServerPlayer player) return;
        WatchedAttributes.SetBool(SalvagedKey, true);
        Salvage(player);
    }
}
