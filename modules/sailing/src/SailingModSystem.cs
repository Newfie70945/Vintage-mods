using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>
/// Realism for sailing (server physics, sail trim, squalls and warnings; client storm visuals).
/// </summary>
public sealed class SailingModSystem : ModSystem
{
    public const string ChannelName = "newfiessailing";
    public const string TrimKey = "newfiessailing:trim";

    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;
    private SailingConfig config = new();
    private readonly Dictionary<long, Entity> boats = new();
    private readonly Dictionary<string, (int Force, long At)> lastWarn = new();
    private readonly Dictionary<long, long> lastOverloadWarn = new();
    private readonly Dictionary<long, double> smoothed = new();
    private SailingVisuals? visuals;
    private long tickListener;
    private double elapsed;
    private readonly Random rng = new();

    // Squall schedule in seconds of mod time.
    private double nextSquallAt;
    private double squallStart = -1, squallDuration;
    private bool squallWarned;
    private double squallFactor = 1;
    private double lastSyncAt = -10;

    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<SailingSyncPacket>();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        visuals = new SailingVisuals(api);
        api.Network.GetChannel(ChannelName).SetMessageHandler<SailingSyncPacket>(p => visuals.SquallFactor = p.Squall);
        visuals.Start();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        serverChannel = api.Network.GetChannel(ChannelName);
        try { config = api.LoadModConfig<SailingConfig>("NewfiesSailing.json") ?? new SailingConfig(); }
        catch (Exception ex) { api.Logger.Warning("[NewfiesSailing] Bad config, using defaults: " + ex.Message); }
        api.StoreModConfig(config, "NewfiesSailing.json");
        ScheduleNextSquall();

        api.Event.OnEntitySpawn += Track;
        api.Event.OnEntityLoaded += Track;
        api.Event.OnEntityDespawn += (e, _) => { boats.Remove(e.EntityId); smoothed.Remove(e.EntityId); lastOverloadWarn.Remove(e.EntityId); };

        api.ChatCommands.Create("sailing")
            .WithDescription("Show wind, sea state and sailing advice at your position")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(args => TextCommandResult.Success(Report(args.Caller.Player as IServerPlayer)))
            .BeginSubCommand("trim")
                .WithDescription("Set the sails of the boat you are riding: full, reefed or furled")
                .WithArgs(api.ChatCommands.Parsers.WordRange("mode", "full", "reefed", "furled"))
                .HandleWith(args => SetTrim(args.Caller.Player as IServerPlayer, args[0] as string))
            .EndSubCommand();

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

    private static SailTrim GetTrim(Entity boat) => (SailTrim)Math.Clamp(boat.WatchedAttributes.GetInt(TrimKey, (int)SailTrim.Full), 0, 2);

    private Entity? BoatOf(IServerPlayer? player)
    {
        if (player?.Entity?.MountedOn == null) return null;
        Entity? best = null;
        double bestDist = 6;
        foreach (Entity b in boats.Values)
        {
            double d = player.Entity.Pos.DistanceTo(b.Pos.XYZ);
            if (d < bestDist) { best = b; bestDist = d; }
        }
        return best;
    }

    private TextCommandResult SetTrim(IServerPlayer? player, string? mode)
    {
        if (!SailingModel.TryParseTrim(mode, out SailTrim trim)) return TextCommandResult.Error("Use full, reefed or furled.");
        Entity? boat = BoatOf(player);
        if (boat == null) return TextCommandResult.Error("You must be riding a sailboat.");
        boat.WatchedAttributes.SetInt(TrimKey, (int)trim);
        boat.WatchedAttributes.MarkPathDirty(TrimKey);
        return TextCommandResult.Success(trim switch
        {
            SailTrim.Full => "Full sail set. Fast in moderate wind, but dangerous in a gale.",
            SailTrim.Reefed => "Sails reefed: slower in light air, safe in strong wind.",
            _ => "Sails furled. The boat will lose way."
        });
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
                NotifySailors("[Sailing] Dark clouds and a line of white water are racing toward you - a squall is coming. Reef the sails (/sailing trim reefed)!");
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
        double speed = Math.Sqrt(wind.X * wind.X + wind.Z * wind.Z);
        return speed * config.KnotsPerWindUnit * squallFactor;
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
        if (elapsed - lastSyncAt >= 1)
        {
            lastSyncAt = elapsed;
            serverChannel?.BroadcastPacket(new SailingSyncPacket { Squall = squallFactor });
        }

        long now = Environment.TickCount64;
        foreach (Entity boat in boats.Values.ToArray())
        {
            if (!boat.Alive || !HasMountedPlayerNear(boat)) continue;
            var motion = boat.SidedPos.Motion;
            double speed = Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z);
            if (speed < config.MinMotion) continue;

            double area = SailingModel.SailArea(GetTrim(boat));
            double knots = KnotsAt(boat.SidedPos.XYZ, out Vec3d wind) * SailingModel.Gust(elapsed, config.GustAmplitude, boat.EntityId % 100);
            double off = SailingModel.AngleOffWind(motion.X, motion.Z, wind.X, wind.Z);
            double target = SailingModel.SpeedMultiplier(knots, off, config.MinSpeedMultiplier, config.MaxSpeedMultiplier, config.ReferenceKnots, config.OverloadKnots, area);

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
            double leeway = SailingModel.LeewayFraction(off, area, drive, config.LeewayStrength) * speed;
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
                NotifySailors("[Sailing] The boat is overpowered and heeling hard! Reef the sails: /sailing trim reefed");
            }
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
            string note = force >= 8 ? " Storm conditions - head for shore!" : force >= 6 ? " Consider reefing." : "";
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
        if (squallFactor > 1.05) text += "\nA squall is passing: wind is up " + $"{(squallFactor - 1) * 100:0}%.";
        Entity? boat = BoatOf(player);
        if (boat != null)
        {
            SailTrim trim = GetTrim(boat);
            text += $"\nSails: {trim.ToString().ToLowerInvariant()}.";
            if (SailingModel.Overpowered(knots, SailingModel.SailArea(trim), config.OverloadKnots)) text += " Overpowered - reef!";
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
