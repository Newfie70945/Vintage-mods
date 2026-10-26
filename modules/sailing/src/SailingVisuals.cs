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
    private bool temporalStorm;
    private double stormSeconds, windRotation = 1.5, windBoost = 1, seasonFactor = 1;
    private double syncedAt;
    private double serverFog, appliedFog;
    private readonly Dictionary<long, int> lastWave = new();
    private readonly Dictionary<long, double> kick = new();
    private double nextGustSound = 5, nextCreakAt;
    private readonly Dictionary<long, double> heaveApplied = new();
    private const string FogModifierKey = "newfiessailing:fog";

    public void ApplySync(SailingSyncPacket p)
    {
        SquallFactor = p.Squall;
        temporalStorm = p.TemporalStorm;
        stormSeconds = p.Seconds;
        windRotation = p.WindRotationAmplitude;
        windBoost = p.WindBoost;
        seasonFactor = p.SeasonFactor;
        serverFog = p.Fog;
        syncedAt = elapsed;
    }

    public SailingVisuals(ICoreClientAPI api)
    {
        capi = api;
        SailingVisualsConfig? loaded = null;
        try { loaded = api.LoadModConfig<SailingVisualsConfig>("NewfiesSailingClient.json"); }
        catch (Exception ex) { api.Logger.Warning("[NewfiesSailing] Bad client config, using defaults: " + ex.Message); }
        cfg = loaded ?? new SailingVisualsConfig();
        api.StoreModConfig(cfg, "NewfiesSailingClient.json");
    }

    public double SpyglassFraction => cfg.SpyglassZoom;

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

        bool onBoat = player.MountedOn != null;
        var boats = capi.World.GetEntitiesAround(player.Pos.XYZ, cfg.Range, cfg.Range / 2f, IsSailBoat);
        foreach (Entity boat in boats)
        {
            Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(boat.Pos.XYZ) ?? new Vec3d();
            if (temporalStorm)
            {
                var (rx, rz) = SailingModel.RotateWind(wind.X, wind.Z, SailingModel.TemporalWindRotation(stormSeconds + (elapsed - syncedAt), windRotation));
                wind = new Vec3d(rx, wind.Y, rz);
            }
            double knots = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z) * cfg.KnotsPerWindUnit * SquallFactor * seasonFactor * SailingModel.FogWindFactor(serverFog) * (temporalStorm ? windBoost : 1);
            int force = SailingModel.StormForce(SailingModel.Beaufort(knots), temporalStorm);

            if (cfg.BobbingEnabled && cfg.BobIntensity > 0)
            {
                var (pitch, roll) = SailingModel.Bob(elapsed, force, boat.EntityId % 64);
                double water = cfg.CapsizeVisualsEnabled ? boat.WatchedAttributes.GetDouble(SailingModSystem.WaterKey, 0) : 0;
                double rough = cfg.BobIntensity * cfg.SeaRoughness;
                boat.Pos.Pitch = (float)(pitch * rough + 0.15 * Math.Clamp((water - 40) / 60.0, 0, 1));
                boat.Pos.Roll = (float)(roll * rough + HeelFor(boat, wind, knots) + WaveKick(boat));
            }

            if (cfg.HeaveEnabled)
            {
                heaveApplied.TryGetValue(boat.EntityId, out double prevHeave);
                double heave = SailingModel.Heave(elapsed, force, boat.EntityId % 64);
                boat.Pos.Y += heave - prevHeave;
                heaveApplied[boat.EntityId] = heave;
            }
            if (cfg.SplashesEnabled) Splash(boat, force);
            if (cfg.WakeEnabled) Wake(boat);
            if (temporalStorm && cfg.StormGlowEnabled) Glow(boat);
        }
        UpdateFog(onBoat || boats.Length > 0);
        if (onBoat && cfg.AmbientSoundsEnabled) AmbientSounds(player);
    }

    private void Play(string name, EntityPlayer player, float volume)
    {
        try { capi.World.PlaySoundAt(new AssetLocation("newfiessailing:sounds/" + name), player.Pos.X, player.Pos.Y, player.Pos.Z, null, true, 24, volume); }
        catch { }
    }

    /// <summary>Wind gusts in rough weather and hull creaks when the boat leans hard.</summary>
    private void AmbientSounds(EntityPlayer player)
    {
        Vec3d wind = capi.World.BlockAccessor.GetWindSpeedAt(player.Pos.XYZ) ?? new Vec3d();
        double knots = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z) * cfg.KnotsPerWindUnit * SquallFactor * seasonFactor * SailingModel.FogWindFactor(serverFog);
        int force = SailingModel.StormForce(SailingModel.Beaufort(knots), temporalStorm);
        if (elapsed >= nextGustSound && force >= 4)
        {
            Play("windgust", player, (float)Math.Min(1.0, force / 10.0));
            nextGustSound = elapsed + 6 + rng.NextDouble() * 6;
        }
        else if (elapsed >= nextGustSound) nextGustSound = elapsed + 4;
        Entity? ride = capi.World.GetNearestEntity(player.Pos.XYZ, 6, 6, IsSailBoat);
        double lean = Math.Abs(ride?.Pos.Roll ?? 0);
        if (force >= 6 && elapsed >= nextCreakAt && lean > 0.2)
        {
            Play("creak", player, 0.7f);
            nextCreakAt = elapsed + 5 + rng.NextDouble() * 5;
        }
    }

    /// <summary>Rogue wave: a sudden lurch that rings out over a couple of seconds, with a big burst of spray.</summary>
    private double WaveKick(Entity boat)
    {
        int seen = boat.WatchedAttributes.GetInt(SailingModSystem.WaveKey, 0);
        lastWave.TryGetValue(boat.EntityId, out int last);
        if (seen != last)
        {
            lastWave[boat.EntityId] = seen;
            kick[boat.EntityId] = (rng.Next(2) == 0 ? -1 : 1) * 0.55;
            Splash(boat, 12);
        }
        kick.TryGetValue(boat.EntityId, out double k);
        if (Math.Abs(k) < 0.002) return 0;
        k *= 0.94;
        kick[boat.EntityId] = k;
        return k * Math.Cos(elapsed * 5);
    }

    /// <summary>Fog banks: applies an ambient fog modifier while the player is on or beside a boat, easing in and out.</summary>
    private void UpdateFog(bool nearBoat)
    {
        double desired = cfg.FogVisualsEnabled && nearBoat ? serverFog * (SpyglassZoom.Active ? 0.5 : 1) : 0;
        appliedFog += (desired - appliedFog) * 0.03;
        if (appliedFog < 0.01)
        {
            capi.Ambient.CurrentModifiers.Remove(FogModifierKey);
            return;
        }
        var mod = new AmbientModifier().EnsurePopulated();
        mod.FogDensity.Value = (float)(0.012 * appliedFog);
        mod.FogDensity.Weight = (float)appliedFog;
        mod.FogColor.Value = new[] { 0.78f, 0.82f, 0.85f };
        mod.FogColor.Weight = (float)appliedFog;
        capi.Ambient.CurrentModifiers[FogModifierKey] = mod;
    }

    /// <summary>Smoothed heel (radians) that leans the hull away from the wind according to trim, wind load and point of sail.</summary>
    private double HeelFor(Entity boat, Vec3d wind, double knots)
    {
        double target = 0;
        var m = boat.Pos.Motion;
        bool capsized = cfg.CapsizeVisualsEnabled && boat.WatchedAttributes.GetBool(SailingModSystem.CapsizedKey, false);
        if (capsized) target = Math.PI;
        else if (cfg.HeelingEnabled && Math.Sqrt(m.X * m.X + m.Z * m.Z) > 0.004)
        {
            double area = SailingModel.SailArea((SailTrim)Math.Clamp(boat.WatchedAttributes.GetInt(SailingModSystem.TrimKey, (int)SailTrim.Full), 0, 2));
            double off = SailingModel.AngleOffWind(m.X, m.Z, wind.X, wind.Z);
            int side = SailingModel.WindSide(m.X, m.Z, wind.X, wind.Z);
            // Wind blowing toward one side leans the hull toward the other.
            target = -side * cfg.HeelDirection * SailingModel.Heel(knots, off, area, cfg.ReferenceKnots);
        }
        heel.TryGetValue(boat.EntityId, out double cur);
        cur += (target - cur) * (capsized ? 0.05 : 0.08);
        heel[boat.EntityId] = cur;
        return cur;
    }

    /// <summary>Foam trailing the stern, spreading in a V.</summary>
    /// <summary>Phosphorescent specks in the wake after dark.</summary>
    private void NightGlowWake(Entity boat, double ux, double uz, int count)
    {
        double daylight = capi.World.Calendar.GetDayLightStrength(boat.Pos.X, boat.Pos.Z);
        double glow = SailingModel.NightGlow(daylight);
        if (glow < 0.15) return;
        Vec3d p = boat.Pos.XYZ;
        var props = new SimpleParticleProperties(
            Math.Max(1, (int)(count * glow)), Math.Max(2, (int)(count * glow) + 2),
            ColorUtil.ToRgba(200, 90, 255, 220),
            new Vec3d(p.X - ux * 2.5 - 0.8, p.Y + 0.05, p.Z - uz * 2.5 - 0.8),
            new Vec3d(p.X - ux * 1.5 + 0.8, p.Y + 0.25, p.Z - uz * 1.5 + 0.8),
            new Vec3f(-0.1f, 0.05f, -0.1f), new Vec3f(0.1f, 0.2f, 0.1f),
            1.8f, 0f, 0.08f, 0.2f, EnumParticleModel.Quad)
        {
            OpacityEvolve = EvolvingNatFloat.create(EnumTransform.Linear, -200),
            GlowLevel = 200
        };
        capi.World.SpawnParticles(props);
    }

    private void Wake(Entity boat)
    {
        var m = boat.Pos.Motion;
        double speed = Math.Sqrt(m.X * m.X + m.Z * m.Z);
        int count = SailingModel.WakeCount(speed);
        if (count <= 0) return;
        double ux = m.X / speed, uz = m.Z / speed;
        if (cfg.NightGlowEnabled) NightGlowWake(boat, ux, uz, count);
        Vec3d p = boat.Pos.XYZ;
        double sx = p.X - ux * 2.0, sz = p.Z - uz * 2.0;
        // Sideways spread perpendicular to the course.
        float vx = (float)(-uz * 0.5), vz = (float)(ux * 0.5);
        var props = new SimpleParticleProperties(
            count, count + 2,
            ColorUtil.ToRgba(150, 240, 248, 255),
            new Vec3d(sx - 0.5, p.Y + 0.05, sz - 0.5),
            new Vec3d(sx + 0.5, p.Y + 0.2, sz + 0.5),
            new Vec3f(-vx, 0.05f, -vz),
            new Vec3f(vx, 0.2f, vz),
            1.6f, 0f, 0.15f, 0.35f, EnumParticleModel.Quad)
        {
            OpacityEvolve = EvolvingNatFloat.create(EnumTransform.Linear, -150),
            SizeEvolve = EvolvingNatFloat.create(EnumTransform.Linear, 0.25f)
        };
        capi.World.SpawnParticles(props);
    }

    /// <summary>Unnatural violet sparks rising from the water around boats in a temporal storm.</summary>
    private void Glow(Entity boat)
    {
        if (rng.NextDouble() > 0.5) return;
        Vec3d p = boat.Pos.XYZ;
        var props = new SimpleParticleProperties(
            2, 5,
            ColorUtil.ToRgba(180, 170, 90, 255),
            new Vec3d(p.X - 3, p.Y, p.Z - 3),
            new Vec3d(p.X + 3, p.Y + 1, p.Z + 3),
            new Vec3f(-0.1f, 0.4f, -0.1f),
            new Vec3f(0.1f, 1.0f, 0.1f),
            2.5f, -0.02f, 0.1f, 0.3f, EnumParticleModel.Quad)
        {
            OpacityEvolve = EvolvingNatFloat.create(EnumTransform.Quadratic, -180),
            GlowLevel = 90
        };
        capi.World.SpawnParticles(props);
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
