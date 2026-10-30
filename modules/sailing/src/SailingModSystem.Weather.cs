using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>Weather systems: wind and gust model, squalls, fog, temporal storms, seasons, lightning, whirlpools, drifting finds and the weather reports.</summary>
public sealed partial class SailingModSystem
{
    private void TrySpawnWreck()
    {
        if (!config.WrecksEnabled || fogIntensity < 0.5 || elapsed - lastWreckAt < config.WreckSpawnCooldownSeconds) return;
        wrecks.RemoveAll(id => !sapi!.World.LoadedEntities.ContainsKey(id));
        if (wrecks.Count >= config.MaxWrecks) return;
        IPlayer? sailor = sapi!.World.AllOnlinePlayers.FirstOrDefault(p => p.Entity?.MountedOn != null);
        if (sailor?.Entity == null) return;
        lastWreckAt = elapsed;
        try
        {
            EntityProperties? type = sapi.World.GetEntityType(new AssetLocation("newfiessailing:driftingwreck"));
            if (type == null) return;
            double angle = rng.NextDouble() * Math.PI * 2, dist = 40 + rng.NextDouble() * 30;
            double x = sailor.Entity.Pos.X + Math.Cos(angle) * dist, z = sailor.Entity.Pos.Z + Math.Sin(angle) * dist, y = sailor.Entity.Pos.Y;
            if (!sapi.World.BlockAccessor.GetBlock(new BlockPos((int)x, (int)(y - 0.6), (int)z)).IsLiquid()) return;
            Entity ent = sapi.World.ClassRegistry.CreateEntity(type);
            ent.ServerPos.SetPos(x, y, z);
            ent.Pos.SetFrom(ent.ServerPos);
            sapi.World.SpawnEntity(ent);
            wrecks.Add(ent.EntityId);
            NotifySailors("[Sailing] Something dark looms in the fog - the hulk of a wreck, adrift.");
        }
        catch (Exception ex) { sapi!.Logger.Warning("[NewfiesSailing] Could not spawn wreck: " + ex.Message); }
    }

    private void TrySpawnBottle()
    {
        if (!config.BottlesEnabled || elapsed < nextBottleAt) return;
        nextBottleAt = elapsed + config.BottleIntervalSeconds * (0.7 + rng.NextDouble() * 0.6);
        bottles.RemoveAll(id => !sapi!.World.LoadedEntities.ContainsKey(id));
        if (bottles.Count >= config.MaxBottles) return;
        IPlayer? sailor = sapi!.World.AllOnlinePlayers.FirstOrDefault(p => p.Entity?.MountedOn != null);
        if (sailor?.Entity == null) return;
        try
        {
            EntityProperties? type = sapi.World.GetEntityType(new AssetLocation("newfiessailing:driftingbottle"));
            if (type == null) return;
            double angle = rng.NextDouble() * Math.PI * 2, dist = 30 + rng.NextDouble() * 50;
            double x = sailor.Entity.Pos.X + Math.Cos(angle) * dist, z = sailor.Entity.Pos.Z + Math.Sin(angle) * dist, y = sailor.Entity.Pos.Y;
            if (!sapi.World.BlockAccessor.GetBlock(new BlockPos((int)x, (int)(y - 0.6), (int)z)).IsLiquid()) return;
            Entity ent = sapi.World.ClassRegistry.CreateEntity(type);
            ent.ServerPos.SetPos(x, y, z);
            ent.Pos.SetFrom(ent.ServerPos);
            sapi.World.SpawnEntity(ent);
            bottles.Add(ent.EntityId);
        }
        catch (Exception ex) { sapi!.Logger.Warning("[NewfiesSailing] Could not spawn bottle: " + ex.Message); }
    }

    /// <summary>Pushes the boat with the tidal current (kept off anchored boats by the caller).</summary>
    private void ApplyCurrent(Entity boat)
    {
        if (!config.CurrentsEnabled || (!config.CurrentsAffectEmptyBoats && !HasMountedPlayerNear(boat))) return;
        var (cx, cz) = SailingModel.Current(boat.SidedPos.X, boat.SidedPos.Z, sapi!.World.Calendar.TotalHours);
        boat.SidedPos.Motion.X += cx * config.CurrentPush;
        boat.SidedPos.Motion.Z += cz * config.CurrentPush;
    }

    /// <summary>Lightning strike on a boat: sails and morale suffer unless a lightning rod carries it away.</summary>
    private void Lightning(Entity boat)
    {
        Vec3d p = boat.Pos.XYZ;
        Play("thunder", p, 96, 1f);
        sapi!.World.SpawnParticles(new SimpleParticleProperties(
            40, 60, ColorUtil.ToRgba(255, 245, 250, 255),
            new Vec3d(p.X - 0.3, p.Y + 1, p.Z - 0.3), new Vec3d(p.X + 0.3, p.Y + 14, p.Z + 0.3),
            new Vec3f(-0.1f, 0f, -0.1f), new Vec3f(0.1f, 0.1f, 0.1f),
            0.35f, 0f, 0.5f, 1.1f, EnumParticleModel.Quad) { GlowLevel = 255 });
        if ((GetUpgrades(boat) & SailingModel.UpgradeLightningRod) != 0)
        {
            NotifyNear(boat, $"[Sailing] Lightning strikes {Nm(boat).ToLowerInvariant()}, but the rod carries it harmlessly into the sea!");
            return;
        }
        SetCond(boat, SailKey, GetCond(boat, SailKey) - 25);
        SetMorale(boat, GetMorale(boat) - 15);
        NotifyNear(boat, $"[Sailing] Lightning strikes {Nm(boat).ToLowerInvariant()}! The sails are scorched and the crew are shaken. A lightning rod would have saved them.");
    }

    private void TrySpawnWhirlpool()
    {
        if (!config.WhirlpoolsEnabled || elapsed - lastWhirlpoolAt < config.WhirlpoolCooldownSeconds) return;
        whirlpools.RemoveAll(id => !sapi!.World.LoadedEntities.ContainsKey(id));
        if (whirlpools.Count >= 1) return;
        IPlayer? sailor = sapi!.World.AllOnlinePlayers.FirstOrDefault(p => p.Entity?.MountedOn != null);
        if (sailor?.Entity == null) return;
        lastWhirlpoolAt = elapsed;
        try
        {
            EntityProperties? type = sapi.World.GetEntityType(new AssetLocation("newfiessailing:whirlpool"));
            if (type == null) return;
            double angle = rng.NextDouble() * Math.PI * 2, dist = 45 + rng.NextDouble() * 25;
            double x = sailor.Entity.Pos.X + Math.Cos(angle) * dist, z = sailor.Entity.Pos.Z + Math.Sin(angle) * dist, y = sailor.Entity.Pos.Y;
            if (!sapi.World.BlockAccessor.GetBlock(new BlockPos((int)x, (int)(y - 0.6), (int)z)).IsLiquid()) return;
            Entity ent = sapi.World.ClassRegistry.CreateEntity(type);
            ent.ServerPos.SetPos(x, y, z);
            ent.Pos.SetFrom(ent.ServerPos);
            sapi.World.SpawnEntity(ent);
            whirlpools.Add(ent.EntityId);
            NotifySailors("[Sailing] The water ahead is turning - a whirlpool is opening where the sky is torn! Steer clear or fight free.");
        }
        catch (Exception ex) { sapi!.Logger.Warning("[NewfiesSailing] Could not spawn whirlpool: " + ex.Message); }
    }

    /// <summary>Called by a whirlpool each tick a boat is near its centre.</summary>
    public void WhirlpoolHit(Entity boat, float dt)
    {
        SetWater(boat, GetWater(boat) + 3 * dt);
        SetCond(boat, HullKey, GetCond(boat, HullKey) - 1.5 * dt);
        SetMorale(boat, GetMorale(boat) - 1 * dt);
        long now = Environment.TickCount64;
        if (!lastWhirlWarn.TryGetValue(boat.EntityId, out long at) || now - at > 15000)
        {
            lastWhirlWarn[boat.EntityId] = now;
            NotifyNear(boat, $"[Sailing] {Nm(boat)} is caught in the whirlpool! Hull groaning, water pouring in!");
        }
    }

    private void NotifyNear(Entity boat, string message)
    {
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
            if (p is IServerPlayer sp && sp.Entity != null && sp.Entity.Pos.DistanceTo(boat.Pos.XYZ) < 24)
                sp.SendMessage(GlobalConstants.InfoLogChatGroup, message, EnumChatType.Notification);
    }

    private void UpdateSeason()
    {
        if (!config.SeasonalWindEnabled) { seasonFactor = 1; return; }
        try
        {
            BlockPos pos = (sapi!.World.AllOnlinePlayers.FirstOrDefault()?.Entity?.Pos.XYZ ?? sapi.World.DefaultSpawnPosition.XYZ).AsBlockPos;
            seasonName = sapi.World.Calendar.GetSeason(pos).ToString();
            seasonFactor = SailingModel.SeasonWindFactor(seasonName);
        }
        catch { seasonFactor = 1; }
    }

    private string Vane(IServerPlayer? player)
    {
        if (player?.Entity == null) return "No position available.";
        double knots = KnotsAt(player.Entity.Pos.XYZ, out Vec3d wind);
        int force = SailingModel.StormForce(SailingModel.Beaufort(knots), temporalStorm);
        double from = SailingModel.WindFromBearing(wind.X, wind.Z);
        if (temporalStorm) from = (from + SailingModel.CompassNoise(elapsed) + 360) % 360;
        return $"Wind vane: out of the {SailingModel.Compass(from)} ({from:0} deg) at {knots:0.#} kn, Force {force} - {SailingModel.BeaufortName(force)}." +
               (temporalStorm ? "\nThe vane spins uselessly." : "");
    }

    private double? SecondsToSquall() => squallStart >= 0 || !config.SquallsEnabled ? null : Math.Max(0, nextSquallAt - elapsed);

    private string Glass(IServerPlayer? player)
    {
        if (player?.Entity == null) return "No position available.";
        double? eta = SecondsToSquall();
        bool active = squallStart >= 0;
        double now = SailingModel.Barometer(eta, active, temporalStorm, elapsed);
        double later = SailingModel.Barometer(eta is double e ? Math.Max(0, e - 30) : null, active, temporalStorm, elapsed + 30);
        KnotsAt(player.Entity.Pos.XYZ, out Vec3d wind);
        double from = SailingModel.WindFromBearing(wind.X, wind.Z);
        string text = $"Weather glass: {now:0} hPa, {SailingModel.Trend(now, later)} - {SailingModel.GlassForecast(now)}.\n" +
                      $"Wind vane points {SailingModel.Compass(from)}; the wind is out of the {SailingModel.Compass(from)}.";
        if (temporalStorm) text += "\nThe glass and vane swing wildly. Something is wrong with the sky.";
        else if (fogIntensity > 0.05) text += "\nThe glass is steady and the air is thick and damp: fog.";
        return text;
    }

    private bool ReadTemporalStorm()
    {
        if (forcedStorm) return true;
        if (!config.TemporalStormsEnabled) return false;
        try
        {
            if (!stabilityLookedUp)
            {
                stabilityLookedUp = true;
                stabilitySystem = sapi!.ModLoader.Systems.FirstOrDefault(m => m.GetType().Name == "SystemTemporalStability");
            }
            if (stabilitySystem == null) return false;
            object? data = Member(stabilitySystem, "StormData");
            object? active = data == null ? null : Member(data, "nowStormActive");
            if (active is bool b) return b;
            if (!stabilityLogged)
            {
                stabilityLogged = true;
                sapi!.Logger.Warning("[NewfiesSailing] Could not read temporal storm state from SystemTemporalStability; an admin can use /sailingadmin to simulate one.");
            }
        }
        catch (Exception ex)
        {
            if (!stabilityLogged) { stabilityLogged = true; sapi!.Logger.Warning("[NewfiesSailing] Temporal storm lookup failed: " + ex.Message); }
        }
        return false;
    }

    private static object? Member(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        Type t = target.GetType();
        return t.GetProperty(name, flags)?.GetValue(target) ?? t.GetField(name, flags)?.GetValue(target);
    }

    private void UpdateTemporalStorm()
    {
        if (elapsed - lastStormCheck < 1) return;
        lastStormCheck = elapsed;
        UpdateSeason();
        bool now = ReadTemporalStorm();
        if (now != temporalStorm)
        {
            temporalStorm = now;
            NotifySailors(now
                ? "[Sailing] The sky tears open - a temporal storm! The wind is veering wildly, compasses spin and things stir in the water."
                : "[Sailing] The temporal storm fades. The sea settles.");
            if (!now) { DespawnBoarders(); LogEvent("storms"); }
        }
        if (temporalStorm) { SpawnBoarder(); TrySpawnWhirlpool(); }
    }

    private void SpawnBoarder()
    {
        if (!config.BoardersEnabled || string.IsNullOrWhiteSpace(config.BoarderEntityCode)) return;
        boarders.RemoveAll(id => !sapi!.World.LoadedEntities.ContainsKey(id));
        if (boarders.Count >= config.MaxBoarders || elapsed - lastBoarderAt < config.BoarderCooldownSeconds) return;
        Entity? boat = boats.Values.FirstOrDefault(b => b.Alive && HasMountedPlayerNear(b));
        if (boat == null) return;
        try
        {
            EntityProperties? type = sapi!.World.GetEntityType(new AssetLocation(config.BoarderEntityCode));
            if (type == null) return;
            Entity ent = sapi.World.ClassRegistry.CreateEntity(type);
            double angle = rng.NextDouble() * Math.PI * 2;
            ent.ServerPos.SetPos(boat.ServerPos.X + Math.Cos(angle) * 4, boat.ServerPos.Y + 0.5, boat.ServerPos.Z + Math.Sin(angle) * 4);
            ent.Pos.SetFrom(ent.ServerPos);
            sapi.World.SpawnEntity(ent);
            boarders.Add(ent.EntityId);
            lastBoarderAt = elapsed;
            NotifySailors("[Sailing] Something pulls itself out of the water beside the boat!");
        }
        catch (Exception ex)
        {
            sapi!.Logger.Warning("[NewfiesSailing] Could not spawn boarder: " + ex.Message);
            lastBoarderAt = elapsed;
        }
    }

    private void DespawnBoarders()
    {
        foreach (long id in boarders)
            if (sapi!.World.LoadedEntities.TryGetValue(id, out Entity? e))
                sapi.World.DespawnEntity(e, new EntityDespawnData { Reason = EnumDespawnReason.Expire });
        boarders.Clear();
    }

    private void ScheduleNextFog()
        => nextFogAt = elapsed + rng.Next(config.FogMinIntervalSeconds, Math.Max(config.FogMinIntervalSeconds, config.FogMaxIntervalSeconds) + 1);

    private void UpdateFog()
    {
        if (!config.FogEnabled) { fogIntensity = 0; return; }
        if (fogStart < 0)
        {
            if (elapsed >= nextFogAt)
            {
                fogStart = elapsed;
                fogDuration = rng.Next(config.FogMinDurationSeconds, Math.Max(config.FogMinDurationSeconds, config.FogMaxDurationSeconds) + 1);
                fogAnnounced = false;
            }
            fogIntensity = 0;
            return;
        }
        double age = elapsed - fogStart;
        fogIntensity = SailingModel.FogIntensity(age, fogDuration);
        TrySpawnWreck();
        if (!fogAnnounced && fogIntensity > 0.3)
        {
            fogAnnounced = true;
            NotifySailors("[Sailing] A fog bank rolls in. Visibility is dropping and the wind is falling light - keep your compass handy.");
        }
        if (age >= fogDuration)
        {
            fogStart = -1;
            fogIntensity = 0;
            ScheduleNextFog();
            NotifySailors("[Sailing] The fog is burning off.");
        }
    }

    /// <summary>A rogue wave slams the boat: ships water, damages the hull and checks its way.</summary>
    private void RogueWave(Entity boat, int force, double sailArea)
    {
        var (water, hull) = SailingModel.RogueWaveImpact(force, sailArea);
        SetWater(boat, GetWater(boat) + water * config.RogueWaveStrength);
        SetCond(boat, HullKey, GetCond(boat, HullKey) - hull * config.RogueWaveStrength * SailingModel.UpgradeHullWearMult(GetUpgrades(boat)));
        SetMorale(boat, GetMorale(boat) - 5);
        Play("waveslam", boat.Pos.XYZ, 48, 0.9f);
        boat.SidedPos.Motion.X *= 0.6;
        boat.SidedPos.Motion.Z *= 0.6;
        boat.WatchedAttributes.SetInt(WaveKey, boat.WatchedAttributes.GetInt(WaveKey, 0) + 1);
        boat.WatchedAttributes.MarkPathDirty(WaveKey);
        NotifyNear(boat, $"[Sailing] A rogue wave slams over the bow of {Nm(boat).ToLowerInvariant()}!");
    }

    private void ScheduleNextSquall()
        => nextSquallAt = elapsed + rng.Next(config.SquallMinIntervalSeconds, Math.Max(config.SquallMinIntervalSeconds, config.SquallMaxIntervalSeconds) + 1);

    private void UpdateSquall()
    {
        if (!config.SquallsEnabled) { squallFactor = 1; return; }
        if (squallStart < 0)
        {
            if (!squallWarned && elapsed >= nextSquallAt - config.SquallWarningSeconds)
            {
                squallWarned = true;
                NotifySailors("[Sailing] Dark clouds and a line of white water are racing toward you - a squall is coming. Reef the sails with your rigging line!");
            }
            if (elapsed >= nextSquallAt)
            {
                squallStart = elapsed;
                squallDuration = rng.Next(config.SquallMinDurationSeconds, Math.Max(config.SquallMinDurationSeconds, config.SquallMaxDurationSeconds) + 1);
                squallWarned = false;
            }
            squallFactor = 1;
            return;
        }
        double age = elapsed - squallStart;
        squallFactor = SailingModel.SquallFactor(age, squallDuration, config.SquallPeakMultiplier);
        if (age >= squallDuration)
        {
            squallStart = -1;
            squallFactor = 1;
            ScheduleNextSquall();
            NotifySailors("[Sailing] The squall has passed. The wind is easing.");
            LogEvent("squalls");
        }
    }

    private void NotifySailors(string message)
    {
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
            if (p is IServerPlayer sp && p.Entity?.MountedOn != null)
                sp.SendMessage(GlobalConstants.InfoLogChatGroup, message, EnumChatType.Notification);
    }

    private double KnotsAt(Vec3d pos, out Vec3d wind)
    {
        wind = sapi!.World.BlockAccessor.GetWindSpeedAt(pos) ?? new Vec3d();
        if (temporalStorm)
        {
            var (rx, rz) = SailingModel.RotateWind(wind.X, wind.Z, SailingModel.TemporalWindRotation(elapsed, config.TemporalStormWindRotation));
            wind = new Vec3d(rx, wind.Y, rz);
        }
        double speed = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        double diurnal = config.DiurnalWindEnabled ? SailingModel.DiurnalWindFactor(sapi.World.Calendar.GetDayLightStrength(pos.X, pos.Z)) : 1;
        return speed * diurnal * config.KnotsPerWindUnit * squallFactor * seasonFactor * SailingModel.FogWindFactor(fogIntensity) * (temporalStorm ? config.TemporalStormWindBoost : 1);
    }

    /// <summary>Wind left at the boat after passing over land upwind (sampled every 4 blocks for 32 blocks, cached 2 s).</summary>
    private double WindShadow(Entity boat, Vec3d wind)
    {
        if (!config.WindShadowEnabled) return 1;
        if (shadowCache.TryGetValue(boat.EntityId, out var cached) && elapsed - cached.At < 2) return cached.Factor;
        double len = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        if (len < 1e-6) return 1;
        double ux = -wind.X / len, uz = -wind.Z / len;
        int blocked = 0;
        var ba = sapi!.World.BlockAccessor;
        for (int i = 1; i <= 8; i++)
        {
            int x = (int)Math.Floor(boat.ServerPos.X + ux * i * 4), z = (int)Math.Floor(boat.ServerPos.Z + uz * i * 4), y0 = (int)Math.Floor(boat.ServerPos.Y);
            for (int dy = 1; dy <= 4; dy++)
            {
                Block block = ba.GetBlock(new BlockPos(x, y0 + dy, z));
                if (block.Id != 0 && !block.IsLiquid()) { blocked++; break; }
            }
        }
        double factor = SailingModel.WindShadowFactor(blocked, 8);
        shadowCache[boat.EntityId] = (factor, elapsed);
        return factor;
    }

    private void WarnSailors()
    {
        if (!config.WarnOnForceChange) return;
        long now = Environment.TickCount64;
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || p.Entity?.MountedOn == null) continue;
            double knots = KnotsAt(p.Entity.Pos.XYZ, out _);
            var wa = p.Entity.WatchedAttributes;
            if (knots > wa.GetDouble(LogPrefix + "topknots", 0)) wa.SetDouble(LogPrefix + "topknots", knots);
            int force = SailingModel.StormForce(SailingModel.Beaufort(knots), temporalStorm);
            if (lastWarn.TryGetValue(sp.PlayerUID, out var last))
            {
                bool bigChange = Math.Abs(last.Force - force) >= 2 || (force >= 8) != (last.Force >= 8);
                if (!bigChange || now - last.At < config.WarnCooldownSeconds * 1000L) continue;
            }
            lastWarn[sp.PlayerUID] = (force, now);
            string note = force >= 8 ? " Storm conditions - head for shore!" : force >= 6 ? " Consider reefing." : "";
            sp.SendMessage(GlobalConstants.InfoLogChatGroup, $"[Sailing] Wind is now Force {force} ({SailingModel.BeaufortName(force)}).{note}", EnumChatType.Notification);
        }
    }

    private string Report(IServerPlayer? player)
    {
        if (player?.Entity == null) return "No position available.";
        double knots = KnotsAt(player.Entity.Pos.XYZ, out Vec3d wind);
        int force = SailingModel.StormForce(SailingModel.Beaufort(knots), temporalStorm);
        double from = SailingModel.WindFromBearing(wind.X, wind.Z);
        if (temporalStorm) from = (from + SailingModel.CompassNoise(elapsed) + 360) % 360;
        string text = $"Wind: {knots:0.#} kn from the {SailingModel.Compass(from)} ({from:0} deg), Force {force} - {SailingModel.BeaufortName(force)}.\n" +
                      $"Sea state: waves about {SailingModel.WaveHeightFor(force):0.#} m. Gust swing: +/-{config.GustAmplitude * 100:0}%.";
        if (seasonName.Length > 0 && Math.Abs(seasonFactor - 1) > 0.01) text += $"\nSeason: {seasonName.ToLowerInvariant()} winds ({(seasonFactor - 1) * 100:+0;-0}% strength).";
        if (config.CurrentsEnabled)
        {
            var (cx, cz) = SailingModel.Current(player.Entity.Pos.X, player.Entity.Pos.Z, sapi!.World.Calendar.TotalHours);
            double mag = Math.Sqrt(cx * cx + cz * cz);
            double toward = (Math.Atan2(cx, -cz) * 180.0 / Math.PI + 360) % 360;
            text += $"\nTide: {SailingModel.TideState(sapi.World.Calendar.TotalHours)}; current {mag * config.MaxCurrentKnots:0.0} kn setting {SailingModel.Compass(toward)}.";
        }
        Entity? shelterBoat = BoatOf(player);
        if (shelterBoat != null && WindShadow(shelterBoat, wind) < 0.85) text += "\nYou are in the lee of the land: the wind is blanketed.";
        if (fogIntensity > 0.05) text += $"\nFog bank: visibility about {SailingModel.FogVisibility(fogIntensity):0} m.";
        if (temporalStorm) text += "\nTEMPORAL STORM: compass unreliable, wind direction wandering, seas unnaturally rough.";
        if (squallFactor > 1.05) text += "\nA squall is passing: wind is up " + $"{(squallFactor - 1) * 100:0}%.";
        Entity? boat = BoatOf(player);
        if (boat != null)
        {
            SailTrim trim = GetTrim(boat);
            text += $"\nSails: {trim.ToString().ToLowerInvariant()}." + (boat.WatchedAttributes.GetBool(AnchorKey, false) ? " Anchor down." : "");
            if (SailingModel.Overpowered(knots, SailingModel.SailArea(trim), config.OverloadKnots)) text += " Overpowered - reef!";
            if (boat.WatchedAttributes.GetString(NameKey, "").Length > 0) text += $"\nVessel: {boat.WatchedAttributes.GetString(NameKey, "")}.";
            if (GetUpgrades(boat) != 0) text += $"\nUpgrades: {SailingModel.UpgradeList(GetUpgrades(boat))}.";
            if (config.MoraleEnabled) text += $"\nCrew morale: {GetMorale(boat):0}% ({SailingModel.MoraleLabel(GetMorale(boat))}).";
            if (IsCapsized(boat)) text += "\nThe boat is CAPSIZED.";
            else if (GetWater(boat) >= 1) text += $"\nWater in the hull: {GetWater(boat):0}%.";
            if (config.WearEnabled)
            {
                double h = GetCond(boat, HullKey), sl = GetCond(boat, SailKey);
                text += $"\nHull {h:0}% ({SailingModel.ConditionLabel(h)}), sails {sl:0}% ({SailingModel.ConditionLabel(sl)}).";
            }
            var motion = boat.SidedPos.Motion;
            if (Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z) > config.MinMotion)
            {
                double off = SailingModel.AngleOffWind(motion.X, motion.Z, wind.X, wind.Z);
                text += $"\nYou are {SailingModel.PointOfSailName(off)}, sail efficiency {SailingModel.PointOfSailEfficiency(off) * 100:0}%.";
            }
        }
        text += force >= 8 ? "\nWarning: gale or worse - full sail is unsafe." :
                force <= 1 ? "\nBecalmed: sailboats will barely move." : "";
        return text;
    }
}
