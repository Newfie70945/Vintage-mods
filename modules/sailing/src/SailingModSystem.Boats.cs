using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>Per-boat simulation: the main tick, sail trim, anchor, wear, flooding, capsizing, recovery, morale and bookkeeping.</summary>
public sealed partial class SailingModSystem
{
    /// <summary>Drops all per-boat bookkeeping for an entity that has unloaded or despawned.</summary>
    private void Forget(long id)
    {
        boats.Remove(id);
        smoothed.Remove(id);
        lastOverloadWarn.Remove(id);
        lastWhirlWarn.Remove(id);
        lastGybeWarn.Remove(id);
        lastGroundWarn.Remove(id);
        prevSide.Remove(id);
        tackPenalty.Remove(id);
        shadowCache.Remove(id);
        lastShanty.Remove(id);
        tows.Remove(id);
        foreach (long key in tows.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) tows.Remove(key);
    }

    private void Track(Entity e)
    {
        string path = e.Code?.Path ?? "";
        if (path.Contains("boat") && config.BoatCodeContains.Any(path.Contains)) boats[e.EntityId] = e;
    }

    private static SailTrim GetTrim(Entity boat) => (SailTrim)Math.Clamp(boat.WatchedAttributes.GetInt(TrimKey, (int)SailTrim.Full), 0, 2);

    private Entity? BoatOf(IServerPlayer? player, bool requireMount = true, double radius = 6)
    {
        if (player?.Entity == null || (requireMount && player.Entity.MountedOn == null)) return null;
        Entity? best = null;
        double bestDist = radius;
        foreach (Entity b in boats.Values)
        {
            double d = player.Entity.Pos.DistanceTo(b.Pos.XYZ);
            if (d < bestDist) { best = b; bestDist = d; }
        }
        return best;
    }

    /// <summary>Rigging line: cycles full, reefed, furled for the boat the player is riding.</summary>
    private string CycleTrim(IServerPlayer player)
    {
        Entity? boat = BoatOf(player);
        if (boat == null) return "You must be riding a sailboat to work the rigging.";
        SailTrim next = GetTrim(boat) switch { SailTrim.Full => SailTrim.Reefed, SailTrim.Reefed => SailTrim.Furled, _ => SailTrim.Full };
        boat.WatchedAttributes.SetInt(TrimKey, (int)next);
        boat.WatchedAttributes.MarkPathDirty(TrimKey);
        return next switch
        {
            SailTrim.Full => "Full sail set. Fast in moderate wind, but dangerous in a gale.",
            SailTrim.Reefed => "Sails reefed: slower in light air, safe in strong wind.",
            _ => "Sails furled. The boat will lose way."
        };
    }

    private static double GetCond(Entity boat, string key) => boat.WatchedAttributes.GetDouble(key, 100);

    private static double GetWater(Entity boat) => boat.WatchedAttributes.GetDouble(WaterKey, 0);

    private static void SetWater(Entity boat, double value)
    {
        value = Math.Clamp(value, 0, 100);
        double old = GetWater(boat);
        boat.WatchedAttributes.SetDouble(WaterKey, value);
        if (Math.Floor(old) != Math.Floor(value)) boat.WatchedAttributes.MarkPathDirty(WaterKey);
    }

    private static double GetMorale(Entity boat) => boat.WatchedAttributes.GetDouble(MoraleKey, 70);

    private static void SetMorale(Entity boat, double value)
    {
        value = Math.Clamp(value, 0, 100);
        double old = GetMorale(boat);
        boat.WatchedAttributes.SetDouble(MoraleKey, value);
        if (Math.Floor(old) != Math.Floor(value)) boat.WatchedAttributes.MarkPathDirty(MoraleKey);
    }

    private static string Nm(Entity boat)
    {
        string name = boat.WatchedAttributes.GetString(NameKey, "");
        return name.Length > 0 ? "The " + name : "The boat";
    }

    private static int GetUpgrades(Entity boat) => boat.WatchedAttributes.GetInt(UpgradeKey, 0);

    private int RidersNear(Entity boat) => sapi!.World.AllOnlinePlayers.Count(p => p.Entity?.MountedOn != null && p.Entity.Pos.DistanceTo(boat.Pos.XYZ) < 6);

    private static bool IsCapsized(Entity boat) => boat.WatchedAttributes.GetBool(CapsizedKey, false);

    private void Capsize(Entity boat)
    {
        LogEvent("capsizes");
        SetMorale(boat, GetMorale(boat) - 30);
        Play("waveslam", boat.Pos.XYZ, 48, 1f);
        boat.WatchedAttributes.SetBool(CapsizedKey, true);
        boat.WatchedAttributes.MarkPathDirty(CapsizedKey);
        SetCond(boat, HullKey, GetCond(boat, HullKey) - 10);
        SetCond(boat, SailKey, GetCond(boat, SailKey) - 20);
        SetWater(boat, GetWater(boat) + 40);
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
            if (p.Entity?.MountedOn != null && p.Entity.Pos.DistanceTo(boat.Pos.XYZ) < 6) p.Entity.TryUnmount();
        NotifyNear(boat, $"[Sailing] {Nm(boat)} capsizes and throws everyone into the sea! Right it with a recovery balloon, and reef next time.");
    }

    /// <summary>
    /// Recovery balloon: three balloons rise from the pack above a capsized or swamped boat within 10 blocks; after 3 s,
    /// once they are overhead, the boat is righted and refloated. Consumes one balloon.
    /// </summary>
    private (string Text, bool Used) Balloon(IServerPlayer player)
    {
        Entity? boat = BoatOf(player, requireMount: false, radius: 10);
        if (boat == null) return ("No sailboat within 10 blocks.", false);
        if (boat.WatchedAttributes.GetBool(SalvageKey, false)) return ("Balloons are already lifting this boat.", false);
        if (!IsCapsized(boat) && GetWater(boat) < 80) return ("This boat is afloat and sound; it does not need a recovery balloon.", false);

        boat.WatchedAttributes.SetBool(SalvageKey, true);
        string[] colors = { "red", "yellow", "blue" };
        double[][] offsets = { new[] { -1.8, 0.4 }, new[] { 1.6, -1.2 }, new[] { 0.2, 1.8 } };
        bool spawned = false;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                EntityProperties? type = sapi!.World.GetEntityType(new AssetLocation("newfiessailing:recoveryballoon-" + colors[i]));
                if (type == null) continue;
                Entity ent = sapi.World.ClassRegistry.CreateEntity(type);
                ent.WatchedAttributes.SetLong(EntityRecoveryBalloon.BoatIdKey, boat.EntityId);
                ent.WatchedAttributes.SetDouble(EntityRecoveryBalloon.OffsetXKey, offsets[i][0]);
                ent.WatchedAttributes.SetDouble(EntityRecoveryBalloon.OffsetZKey, offsets[i][1]);
                ent.ServerPos.SetPos(boat.ServerPos.X + offsets[i][0], boat.ServerPos.Y, boat.ServerPos.Z + offsets[i][1]);
                ent.Pos.SetFrom(ent.ServerPos);
                sapi.World.SpawnEntity(ent);
                spawned = true;
            }
            catch (Exception ex) { sapi!.Logger.Warning("[NewfiesSailing] Could not spawn recovery balloon: " + ex.Message); }
        }
        if (!spawned) sapi!.Logger.Warning("[NewfiesSailing] Recovery balloon entity type not found; boat will still be refloated.");

        long boatId = boat.EntityId;
        string playerUid = player.PlayerUID;
        sapi!.Event.RegisterCallback(_ => FinishSalvage(boatId, playerUid), 3000);
        return ("Three bright balloons shoot up from the pack and inflate overhead... hold on!", true);
    }

    private void FinishSalvage(long boatId, string playerUid)
    {
        Entity? boat = sapi!.World.GetEntityById(boatId);
        if (boat == null) return;
        boat.WatchedAttributes.SetBool(SalvageKey, false);
        boat.WatchedAttributes.SetBool(CapsizedKey, false);
        boat.WatchedAttributes.MarkPathDirty(CapsizedKey);
        SetWater(boat, Math.Min(GetWater(boat), 35));
        SetCond(boat, HullKey, Math.Max(GetCond(boat, HullKey), 25));
        smoothed.Remove(boat.EntityId);
        IPlayer? rescuer = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == playerUid);
        if (rescuer?.Entity != null)
            rescuer.Entity.WatchedAttributes.SetInt(LogPrefix + "rescues", rescuer.Entity.WatchedAttributes.GetInt(LogPrefix + "rescues", 0) + 1);
        SetMorale(boat, GetMorale(boat) + 15);
        Play("balloon", boat.Pos.XYZ, 40, 1f);
        BalloonBurst(boat.Pos.XYZ);
        NotifyNear(boat, "[Sailing] The balloons haul the hull upright and the boat is afloat again - bail it out and patch the hull.");
    }

    private void BalloonBurst(Vec3d p)
    {
        int[] colors = { ColorUtil.ToRgba(255, 230, 60, 50), ColorUtil.ToRgba(255, 240, 200, 60), ColorUtil.ToRgba(255, 60, 120, 230) };
        foreach (int color in colors)
        {
            sapi!.World.SpawnParticles(new SimpleParticleProperties(
                14, 18, color,
                new Vec3d(p.X - 2, p.Y + 0.5, p.Z - 2), new Vec3d(p.X + 2, p.Y + 2, p.Z + 2),
                new Vec3f(-0.4f, 1.2f, -0.4f), new Vec3f(0.4f, 2.0f, 0.4f),
                3f, -0.05f, 0.3f, 0.6f, EnumParticleModel.Cube));
        }
    }

    /// <summary>Bailing bucket: removes 30% water from a boat within 6 blocks.</summary>
    private (string Text, bool Used) Bail(IServerPlayer player)
    {
        Entity? boat = BoatOf(player, requireMount: false);
        if (boat == null) return ("Stand within 6 blocks of a sailboat to bail it.", false);
        double water = GetWater(boat);
        if (water < 1) return ("The bilge is dry.", false);
        SetWater(boat, water - 30);
        return ($"You bail furiously. Water in the hull: {GetWater(boat):0}%.", true);
    }

    private static void SetCond(Entity boat, string key, double value)
    {
        value = Math.Clamp(value, 0, 100);
        double old = GetCond(boat, key);
        boat.WatchedAttributes.SetDouble(key, value);
        if (Math.Floor(old) != Math.Floor(value)) boat.WatchedAttributes.MarkPathDirty(key);
    }

    /// <summary>Repair kit: restores the more damaged of hull and sails on a boat within 6 blocks. Returns whether a use was spent.</summary>
    private (string Text, bool Used) RepairBoat(IServerPlayer player)
    {
        Entity? boat = BoatOf(player, requireMount: false);
        if (boat == null) return ("Stand within 6 blocks of a sailboat to repair it.", false);
        double hull = GetCond(boat, HullKey), sail = GetCond(boat, SailKey);
        if (hull >= 99.5 && sail >= 99.5) return ("The hull and sails are already in excellent shape.", false);
        bool fixHull = hull <= sail;
        string key = fixHull ? HullKey : SailKey;
        SetCond(boat, key, GetCond(boat, key) + SailingModel.RepairPerItem);
        player.Entity.WatchedAttributes.SetInt(LogPrefix + "repairs", player.Entity.WatchedAttributes.GetInt(LogPrefix + "repairs", 0) + 1);
        return ($"Repaired the {(fixHull ? "hull" : "sails")}: now {GetCond(boat, key):0}%.", true);
    }

    private string ToggleAnchor(IServerPlayer? player)
    {
        Entity? boat = BoatOf(player);
        if (boat == null) return "You must be riding a sailboat to use the anchor.";
        bool down = !boat.WatchedAttributes.GetBool(AnchorKey, false);
        boat.WatchedAttributes.SetBool(AnchorKey, down);
        boat.WatchedAttributes.MarkPathDirty(AnchorKey);
        return down
            ? $"Anchor dropped. It holds against about {config.AnchorHoldKnots:0} kn of wind."
            : "Anchor raised. You are free to sail.";
    }

    /// <summary>Shoals and groundings: scrapes the hull and slows the boat in shallow water or on land.</summary>
    private double Grounding(Entity boat, double speed, int up, float dt)
    {
        if (!config.GroundingEnabled) return 1;
        var ba = sapi!.World.BlockAccessor;
        int x = (int)Math.Floor(boat.ServerPos.X), z = (int)Math.Floor(boat.ServerPos.Z), y = (int)Math.Floor(boat.ServerPos.Y - 0.6);
        bool keel = (up & SailingModel.UpgradeKeel) != 0;
        int depth = !ba.GetBlock(new BlockPos(x, y, z)).IsLiquid() ? 0
                  : !ba.GetBlock(new BlockPos(x, y - 1, z)).IsLiquid() ? 1
                  : keel && !ba.GetBlock(new BlockPos(x, y - 2, z)).IsLiquid() ? 1 : 2;
        if (depth >= 2) return 1;
        SetCond(boat, HullKey, GetCond(boat, HullKey) - SailingModel.GroundingWear(depth, speed, keel) * dt);
        long now = Environment.TickCount64;
        if (!lastGroundWarn.TryGetValue(boat.EntityId, out long at) || now - at > 20000)
        {
            lastGroundWarn[boat.EntityId] = now;
            NotifyNear(boat, depth == 0
                ? $"[Sailing] {Nm(boat)} has run aground!"
                : $"[Sailing] The keel of {Nm(boat).ToLowerInvariant()} scrapes bottom - shallow water ahead!");
        }
        return SailingModel.GroundingFactor(depth);
    }

    /// <summary>Tracks the boat crossing the wind: going about costs momentum, an accidental gybe whips the boom across.</summary>
    private double TackFactor(Entity boat, Vec3d motion, Vec3d wind, double off, double appKnots, float dt)
    {
        if (!config.TackingEnabled) return 1;
        long id = boat.EntityId;
        double f = tackPenalty.TryGetValue(id, out double v) ? v : 1;
        if (off >= 20 && off <= 170)
        {
            int side = SailingModel.WindSide(motion.X, motion.Z, wind.X, wind.Z);
            if (side != 0)
            {
                if (prevSide.TryGetValue(id, out int prev) && prev != 0 && prev != side)
                {
                    if (off < 90) f = Math.Min(f, SailingModel.TackMomentum(appKnots));
                    else
                    {
                        double dmg = SailingModel.GybeDamage(appKnots);
                        long now = Environment.TickCount64;
                        if (dmg > 0 && (!lastGybeWarn.TryGetValue(id, out long at) || now - at > 8000))
                        {
                            lastGybeWarn[id] = now;
                            SetCond(boat, SailKey, GetCond(boat, SailKey) - dmg);
                            SetMorale(boat, GetMorale(boat) - 2);
                            NotifyNear(boat, "[Sailing] Gybe! The boom crashes across and the rigging howls.");
                        }
                    }
                }
                prevSide[id] = side;
            }
        }
        f += (1 - f) * Math.Min(1, dt / 4.0);
        tackPenalty[id] = f;
        return f;
    }

    private bool HasMountedPlayerNear(Entity boat)
    {
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
        {
            var ent = p.Entity;
            if (ent?.MountedOn != null && ent.Pos.DistanceTo(boat.Pos.XYZ) < 6) return true;
        }
        return false;
    }

    private void OnTick(float dt)
    {
        elapsed += dt;
        UpdateSquall();
        UpdateFog();
        UpdateTemporalStorm();
        TrackDistance();
        TrySpawnBottle();
        ApplyTows();
        if (elapsed - lastSyncAt >= 1)
        {
            lastSyncAt = elapsed;
            serverChannel?.BroadcastPacket(new SailingSyncPacket { Squall = squallFactor, TemporalStorm = temporalStorm, Seconds = elapsed, WindRotationAmplitude = config.TemporalStormWindRotation, WindBoost = config.TemporalStormWindBoost, SeasonFactor = seasonFactor, Fog = fogIntensity });
        }

        long now = Environment.TickCount64;
        foreach (Entity boat in boats.Values.ToArray())
        {
            if (!boat.Alive) continue;
            if (IsCapsized(boat))
            {
                boat.SidedPos.Motion.X = 0;
                boat.SidedPos.Motion.Z = 0;
                SetWater(boat, GetWater(boat) + 0.5 * dt);
                continue;
            }
            if (boat.WatchedAttributes.GetBool(AnchorKey, false))
            {
                HoldAnchor(boat, now);
                continue;
            }
            ApplyCurrent(boat);
            int up = GetUpgrades(boat);
            if (!HasMountedPlayerNear(boat)) continue;
            var motion = boat.SidedPos.Motion;
            double speed = Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z);
            if (speed < config.MinMotion) continue;

            double area = SailingModel.SailArea(GetTrim(boat));
            double knots = KnotsAt(boat.SidedPos.XYZ, out Vec3d wind) * SailingModel.Gust(elapsed, config.GustAmplitude * (temporalStorm ? 2 : 1), boat.EntityId % 100);
            knots *= WindShadow(boat, wind);
            double trueKnots = knots;
            if (config.ApparentWindEnabled)
            {
                var (ax, az, apparent) = SailingModel.ApparentWind(wind.X, wind.Z, trueKnots, motion.X, motion.Z, config.MotionToKnots);
                knots = apparent;
                wind = new Vec3d(ax, wind.Y, az);
            }
            double off = SailingModel.AngleOffWind(motion.X, motion.Z, wind.X, wind.Z);
            double target = SailingModel.SpeedMultiplier(knots, off, config.MinSpeedMultiplier, config.MaxSpeedMultiplier * SailingModel.UpgradeSpeedMult(up), config.ReferenceKnots, config.OverloadKnots, area);
            if (config.WearEnabled)
            {
                int force = SailingModel.StormForce(SailingModel.Beaufort(trueKnots), temporalStorm);
                var (hullWear, sailWear) = SailingModel.WearPerSecond(force, SailingModel.Overpowered(knots, area, config.OverloadKnots), temporalStorm, area);
                double hullBefore = GetCond(boat, HullKey), sailBefore = GetCond(boat, SailKey);
                double hullNow = hullBefore - hullWear * config.WearRate * SailingModel.UpgradeHullWearMult(up) * dt, sailNow = sailBefore - sailWear * config.WearRate * SailingModel.UpgradeSailWearMult(up) * dt;
                SetCond(boat, HullKey, hullNow);
                SetCond(boat, SailKey, sailNow);
                WarnCondition(boat, "hull", hullBefore, hullNow);
                WarnCondition(boat, "sails", sailBefore, sailNow);
                target *= SailingModel.ConditionSpeedFactor(GetCond(boat, HullKey), GetCond(boat, SailKey));
            }
            int seaForce = SailingModel.StormForce(SailingModel.Beaufort(trueKnots), temporalStorm);
            if (config.MoraleEnabled)
            {
                double moraleBefore = GetMorale(boat);
                SetMorale(boat, moraleBefore + SailingModel.MoraleChangePerSecond(seaForce, temporalStorm, fogIntensity, GetCond(boat, HullKey), GetWater(boat)) * dt);
                double moraleNow = GetMorale(boat);
                if (moraleBefore > 30 && moraleNow <= 30) NotifyNear(boat, "[Sailing] The crew is grumbling. A shanty on the concertina would help.");
                else if (moraleBefore > 10 && moraleNow <= 10) NotifyNear(boat, "[Sailing] The crew is close to mutiny! Their hands are slow on the ropes.");
                target *= SailingModel.MoraleSpeedFactor(moraleNow);
            }
            target *= TackFactor(boat, motion, wind, off, knots, dt) * Grounding(boat, speed, up, dt);
            if (config.CrowdingEnabled) target *= SailingModel.CrowdFactor(RidersNear(boat));
            if (config.LightningEnabled && rng.NextDouble() < SailingModel.LightningChancePerSecond(seaForce, squallFactor, temporalStorm) * dt) Lightning(boat);
            if (config.HeadSeasEnabled) target *= SailingModel.HeadSeaFactor(seaForce, off);
            if (config.RogueWavesEnabled && rng.NextDouble() < SailingModel.RogueWaveChancePerSecond(seaForce) * dt) RogueWave(boat, seaForce, area);
            if (config.SwampingEnabled)
            {
                double before = GetWater(boat);
                double after = before + (SailingModel.FloodPerSecond(seaForce, GetCond(boat, HullKey)) * SailingModel.UpgradeFloodMult(up) - SailingModel.NaturalDrainPerSecond(seaForce, GetCond(boat, HullKey))) * dt;
                SetWater(boat, after);
                if (before < 100 && GetWater(boat) >= 100) NotifyNear(boat, $"[Sailing] {Nm(boat)} is swamped! She is barely moving. Bail with a bucket, patch the hull, or use a recovery balloon.");
                else if (before < 60 && after >= 60) NotifyNear(boat, "[Sailing] Water is sloshing in the bilge - the boat is getting heavy!");
                target *= SailingModel.WaterSpeedFactor(GetWater(boat));
            }
            if (config.CapsizingEnabled && rng.NextDouble() < SailingModel.CapsizeChancePerSecond(knots, area, off, config.OverloadKnots, config.CapsizeSeverity * SailingModel.UpgradeCapsizeMult(up)) * dt)
            {
                Capsize(boat);
                continue;
            }

            smoothed.TryGetValue(boat.EntityId, out double prev);
            if (prev <= 0) prev = 1;
            double next = prev + (target - prev) * config.SpeedSmoothing;
            smoothed[boat.EntityId] = next;

            // Scale only the change from last tick so the boat's own physics still determine its base speed.
            double scale = next / prev;
            motion.X *= scale;
            motion.Z *= scale;

            // Leeway: the wind pushes the hull sideways, most when beating.
            double drive = Math.Clamp(knots * area / Math.Max(1, config.ReferenceKnots), 0, 1);
            double leeway = SailingModel.LeewayFraction(off, area, drive, config.LeewayStrength * SailingModel.UpgradeLeewayMult(up)) * speed;
            double windLen = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
            if (leeway > 0 && windLen > 1e-6)
            {
                double ux = motion.X / speed, uz = motion.Z / speed, wx = wind.X / windLen, wz = wind.Z / windLen;
                double dot = ux * wx + uz * wz;
                double lx = wx - dot * ux, lz = wz - dot * uz;
                double ll = Math.Sqrt(lx * lx + lz * lz);
                if (ll > 1e-6) { motion.X += lx / ll * leeway; motion.Z += lz / ll * leeway; }
            }

            if (SailingModel.Overpowered(knots, area, config.OverloadKnots)
                && (!lastOverloadWarn.TryGetValue(boat.EntityId, out long at) || now - at > 20000))
            {
                lastOverloadWarn[boat.EntityId] = now;
                NotifyNear(boat, $"[Sailing] {Nm(boat)} is overpowered and heeling hard! Work the rigging line to reef!");
            }
        }
        WarnSailors();
    }

    private void WarnCondition(Entity boat, string part, double before, double after)
    {
        foreach (double threshold in new[] { 50.0, 25.0, 10.0 })
        {
            if (before > threshold && after <= threshold)
            {
                NotifyNear(boat, part == "hull"
                    ? $"[Sailing] The hull of {Nm(boat).ToLowerInvariant()} is at {threshold:0}% and taking on water! Use a repair kit!"
                    : $"[Sailing] The sails of {Nm(boat).ToLowerInvariant()} are at {threshold:0}% and in tatters! Use a repair kit!");
                return;
            }
        }
    }

    private void HoldAnchor(Entity boat, long now)
    {
        var motion = boat.SidedPos.Motion;
        motion.X *= 0.2;
        motion.Z *= 0.2;
        double knots = KnotsAt(boat.SidedPos.XYZ, out Vec3d wind);
        double drag = SailingModel.AnchorDrag(knots, config.AnchorHoldKnots);
        double len = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        if (drag <= 0 || len < 1e-6) return;
        motion.X += wind.X / len * 0.012 * drag;
        motion.Z += wind.Z / len * 0.012 * drag;
        if (HasMountedPlayerNear(boat) && (!lastOverloadWarn.TryGetValue(boat.EntityId, out long at) || now - at > 20000))
        {
            lastOverloadWarn[boat.EntityId] = now;
            NotifyNear(boat, $"[Sailing] The anchor of {Nm(boat).ToLowerInvariant()} is dragging in this wind!");
        }
    }
}
