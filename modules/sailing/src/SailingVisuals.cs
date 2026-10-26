using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace NewfiesSailing;

/// <summary>
/// Client-side storm visuals: hulls pitch and roll with the sea state and boats throw spray into rough water.
/// Purely cosmetic; it never changes motion.
/// </summary>
public sealed class SailingVisuals
{
    private readonly ICoreClientAPI capi;
    private readonly SailingVisualsConfig cfg;
    private long listener;
    private double elapsed;
    private readonly Random rng = new();
    private readonly Dictionary<long, double> heel = new();

    /// <summary>Server-synced squall wind multiplier.</summary>
    public double SquallFactor { get; set; } = 1;

    public SailingVisuals(ICoreClientAPI api)
    {
        capi = api;
        SailingVisualsConfig? loaded = null;
        try { loaded = api.LoadModConfig<SailingVisualsConfig>("NewfiesSailingClient.json"); }
        catch (Exception ex) { api.Logger.Warning("[NewfiesSailing] Bad client config, using defaults: " + ex.Message); }
        cfg = loaded ?? new SailingVisualsConfig();
        api.StoreModConfig(cfg, "NewfiesSailingClient.json");
    }

    public void Start() => listener = capi.Event.RegisterGameTickListener(OnTick, 40);

    public void Stop()
    {
        if (listener != 0) capi.Event.UnregisterGameTickListener(listener);
        listener = 0;
    }

    private bool IsSailBoat(Entity e)
    {
        string path = e.Code?.Path ?? "";
        return path.Contains("boat") && cfg.BoatCodeContains.Any(path.Contains);
    }

    private void OnTick(float dt)
    {
        var player = capi.World.Player?.Entity;
        if (player == null) return;
        elapsed += dt;

        var boats = capi.World.GetEntitiesAround(player.Pos.XYZ, cfg.Range, cfg.Range / 2f, IsSailBoat);
        foreach (Entity boat in boats)
        {
            Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(boat.Pos.XYZ) ?? new Vec3d();
            double knots = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z) * cfg.KnotsPerWindUnit * SquallFactor;
            int force = SailingModel.Beaufort(knots);

            if (cfg.BobbingEnabled && cfg.BobIntensity > 0)
            {
                var (pitch, roll) = SailingModel.Bob(elapsed, force, boat.EntityId % 64);
                boat.Pos.Pitch = (float)(pitch * cfg.BobIntensity);
                boat.Pos.Roll = (float)(roll * cfg.BobIntensity + HeelFor(boat, wind, knots));
            }

            if (cfg.SplashesEnabled) Splash(boat, force);
        }
    }

    /// <summary>Smoothed heel (radians) that leans the hull away from the wind according to trim, wind load and point of sail.</summary>
    private double HeelFor(Entity boat, Vec3d wind, double knots)
    {
        double target = 0;
        var m = boat.Pos.Motion;
        if (cfg.HeelingEnabled && Math.Sqrt(m.X * m.X + m.Z * m.Z) > 0.004)
        {
            double area = SailingModel.SailArea((SailTrim)Math.Clamp(boat.WatchedAttributes.GetInt(SailingModSystem.TrimKey, (int)SailTrim.Full), 0, 2));
            double off = SailingModel.AngleOffWind(m.X, m.Z, wind.X, wind.Z);
            int side = SailingModel.WindSide(m.X, m.Z, wind.X, wind.Z);
            // Wind blowing toward one side leans the hull toward the other.
            target = -side * cfg.HeelDirection * SailingModel.Heel(knots, off, area, cfg.ReferenceKnots);
        }
        heel.TryGetValue(boat.EntityId, out double cur);
        cur += (target - cur) * 0.08;
        heel[boat.EntityId] = cur;
        return cur;
    }

    private void Splash(Entity boat, int force)
    {
        double speed = Math.Sqrt(boat.Pos.Motion.X * boat.Pos.Motion.X + boat.Pos.Motion.Z * boat.Pos.Motion.Z);
        int count = SailingModel.SplashCount(force, speed);
        if (count <= 0 || rng.NextDouble() > 0.6) return;

        Vec3d p = boat.Pos.XYZ;
        float up = 0.8f + force * 0.12f;
        var props = new SimpleParticleProperties(
            count, count + 2,
            ColorUtil.ToRgba(190, 235, 245, 255),
            new Vec3d(p.X - 1.2, p.Y + 0.1, p.Z - 1.2),
            new Vec3d(p.X + 1.2, p.Y + 0.5, p.Z + 1.2),
            new Vec3f(-0.6f, up * 0.6f, -0.6f),
            new Vec3f(0.6f, up, 0.6f),
            0.9f, 1f, 0.08f, 0.22f, EnumParticleModel.Quad)
        {
            OpacityEvolve = EvolvingNatFloat.create(EnumTransform.Linear, -190),
            SizeEvolve = EvolvingNatFloat.create(EnumTransform.Linear, -0.1f)
        };
        capi.World.SpawnParticles(props);
    }
}
