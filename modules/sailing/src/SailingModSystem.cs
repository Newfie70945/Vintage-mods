using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// Realism for sailing (server physics and warnings, client storm visuals): wind-dependent boat speed by point of sail, gusts, storm slowdown,
/// force-change warnings for sailors and a /sailing conditions report.
/// </summary>
public sealed class SailingModSystem : ModSystem
{
    private ICoreServerAPI? sapi;
    private SailingConfig config = new();
    private readonly Dictionary<long, Entity> boats = new();
    private readonly Dictionary<string, (int Force, long At)> lastWarn = new();
    private readonly Dictionary<long, double> smoothed = new();
    private long tickListener;
    private double elapsed;

    private SailingVisuals? visuals;

    public override void StartClientSide(ICoreClientAPI api)
    {
        visuals = new SailingVisuals(api);
        visuals.Start();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        try { config = api.LoadModConfig<SailingConfig>("NewfiesSailing.json") ?? new SailingConfig(); }
        catch (Exception ex) { api.Logger.Warning("[NewfiesSailing] Bad config, using defaults: " + ex.Message); }
        api.StoreModConfig(config, "NewfiesSailing.json");

        api.Event.OnEntitySpawn += Track;
        api.Event.OnEntityLoaded += Track;
        api.Event.OnEntityDespawn += (e, _) => { boats.Remove(e.EntityId); smoothed.Remove(e.EntityId); };

        api.ChatCommands.Create("sailing")
            .WithDescription("Show wind, sea state and sailing advice at your position")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(args => TextCommandResult.Success(Report(args.Caller.Player as IServerPlayer)));

        tickListener = api.Event.RegisterGameTickListener(OnTick, Math.Max(50, config.TickMilliseconds));
    }

    public override void Dispose()
    {
        visuals?.Stop();
        if (sapi != null && tickListener != 0) sapi.Event.UnregisterGameTickListener(tickListener);
        boats.Clear();
        base.Dispose();
    }

    private void Track(Entity e)
    {
        string path = e.Code?.Path ?? "";
        if (path.Contains("boat") && config.BoatCodeContains.Any(path.Contains)) boats[e.EntityId] = e;
    }

    private double KnotsAt(Vec3d pos, out Vec3d wind)
    {
        wind = sapi!.World.BlockAccessor.GetWindSpeedAt(pos) ?? new Vec3d();
        double speed = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        return speed * config.KnotsPerWindUnit;
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
        foreach (Entity boat in boats.Values.ToArray())
        {
            if (!boat.Alive || !HasMountedPlayerNear(boat)) continue;
            var motion = boat.SidedPos.Motion;
            double speed = Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z);
            if (speed < config.MinMotion) continue;

            double knots = KnotsAt(boat.SidedPos.XYZ, out Vec3d wind) * SailingModel.Gust(elapsed, config.GustAmplitude, boat.EntityId % 100);
            double off = SailingModel.AngleOffWind(motion.X, motion.Z, wind.X, wind.Z);
            double target = SailingModel.SpeedMultiplier(knots, off, config.MinSpeedMultiplier, config.MaxSpeedMultiplier, config.ReferenceKnots, config.StormKnots);

            smoothed.TryGetValue(boat.EntityId, out double prev);
            if (prev <= 0) prev = 1;
            double next = prev + (target - prev) * config.SpeedSmoothing;
            smoothed[boat.EntityId] = next;

            // Scale only the change from last tick so the boat's own physics still determine its base speed.
            double scale = next / prev;
            motion.X *= scale;
            motion.Z *= scale;
        }
        WarnSailors();
    }

    private void WarnSailors()
    {
        if (!config.WarnOnForceChange) return;
        long now = Environment.TickCount64;
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || p.Entity?.MountedOn == null) continue;
            double knots = KnotsAt(p.Entity.Pos.XYZ, out _);
            int force = SailingModel.Beaufort(knots);
            if (lastWarn.TryGetValue(sp.PlayerUID, out var last))
            {
                bool bigChange = Math.Abs(last.Force - force) >= 2 || (force >= 8) != (last.Force >= 8);
                if (!bigChange || now - last.At < config.WarnCooldownSeconds * 1000L) continue;
            }
            lastWarn[sp.PlayerUID] = (force, now);
            string note = force >= 8 ? " Storm conditions - head for shore!" : force >= 6 ? " Consider shortening sail." : "";
            sp.SendMessage(GlobalConstants.InfoLogChatGroup, $"[Sailing] Wind is now Force {force} ({SailingModel.BeaufortName(force)}).{note}", EnumChatType.Notification);
        }
    }

    private string Report(IServerPlayer? player)
    {
        if (player?.Entity == null) return "No position available.";
        double knots = KnotsAt(player.Entity.Pos.XYZ, out Vec3d wind);
        int force = SailingModel.Beaufort(knots);
        double from = SailingModel.WindFromBearing(wind.X, wind.Z);
        string text = $"Wind: {knots:0.#} kn from the {SailingModel.Compass(from)} ({from:0} deg), Force {force} - {SailingModel.BeaufortName(force)}.\n" +
                      $"Sea state: waves about {SailingModel.WaveHeightFor(force):0.#} m. Gust swing: +/-{config.GustAmplitude * 100:0}%.";
        var motion = player.Entity.MountedOn != null ? player.Entity.Pos.Motion : null;
        if (motion != null && Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z) > config.MinMotion)
        {
            double off = SailingModel.AngleOffWind(motion.X, motion.Z, wind.X, wind.Z);
            text += $"\nYou are {SailingModel.PointOfSailName(off)}, sail efficiency {SailingModel.PointOfSailEfficiency(off) * 100:0}%.";
        }
        text += force >= 8 ? "\nWarning: gale or worse - boats are heavily slowed and unsafe." :
                force <= 1 ? "\nBecalmed: sailboats will barely move." : "";
        return text;
    }
}
