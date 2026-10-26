using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace NewfiesSailing;

/// <summary>
/// A temporal whirlpool: spins, pulls nearby boats toward its centre in a spiral and damages those that reach it.
/// Lives for about 90 seconds. Boats can fight free with sail, oars or by anchoring (which only halves the pull).
/// </summary>
public sealed class EntityWhirlpool : Entity
{
    public const double Radius = 16;
    public const double LifeSeconds = 90;
    private double age;

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (World.Side == EnumAppSide.Client)
        {
            SpawnSwirl();
            return;
        }

        age += dt;
        if (age > LifeSeconds) { Die(EnumDespawnReason.Expire); return; }

        ServerPos.Yaw += dt * 1.8f;
        Pos.SetFrom(ServerPos);

        var sys = World.Api.ModLoader.GetModSystem<SailingModSystem>();
        foreach (Entity boat in World.GetEntitiesAround(ServerPos.XYZ, (float)Radius, 6, e => (e.Code?.Path ?? "").Contains("boat")))
        {
            double dx = ServerPos.X - boat.ServerPos.X, dz = ServerPos.Z - boat.ServerPos.Z, d = Math.Sqrt(dx * dx + dz * dz);
            var (pull, swirl) = SailingModel.WhirlpoolForce(d, Radius);
            if (pull <= 0) continue;
            double hold = boat.WatchedAttributes.GetBool(SailingModSystem.AnchorKey, false) ? 0.5 : 1;
            boat.ServerPos.Motion.X += (dx / d * pull + -dz / d * swirl) * hold;
            boat.ServerPos.Motion.Z += (dz / d * pull + dx / d * swirl) * hold;
            if (d < 3) sys?.WhirlpoolHit(boat, dt);
        }
    }

    private void SpawnSwirl()
    {
        double a = World.Rand.NextDouble() * Math.PI * 2, r = 3 + World.Rand.NextDouble() * 11;
        double x = Pos.X + Math.Cos(a) * r, z = Pos.Z + Math.Sin(a) * r;
        float vx = (float)(-Math.Sin(a) * 2.2), vz = (float)(Math.Cos(a) * 2.2);
        World.SpawnParticles(new SimpleParticleProperties(
            1, 2, ColorUtil.ToRgba(190, 210, 235, 245),
            new Vec3d(x, Pos.Y + 0.1, z), new Vec3d(x + 0.3, Pos.Y + 0.3, z + 0.3),
            new Vec3f(vx, 0.1f, vz), new Vec3f(vx, 0.3f, vz),
            1.2f, 0f, 0.2f, 0.45f, EnumParticleModel.Quad));
    }
}
