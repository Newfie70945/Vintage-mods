using System.Reflection;
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
    public const string AnchorKey = "newfiessailing:anchor";
    public const string HullKey = "newfiessailing:hull";
    public const string SailKey = "newfiessailing:sail";
    private const string LogPrefix = "newfiessailing:log:";

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

    // Temporal storm state (read from the game by reflection so the mod has no compile-time dependency on VSSurvivalMod).
    private bool temporalStorm;
    private bool forcedStorm;
    private double lastStormCheck = -10;
    private ModSystem? stabilitySystem;
    private bool stabilityLookedUp, stabilityLogged;
    private readonly List<long> boarders = new();
    private double lastBoarderAt = -1000;
    private double seasonFactor = 1;
    private string seasonName = "";
    private readonly Dictionary<string, Vec3d> lastPlayerPos = new();

    public override void Start(ICoreAPI api)
    {
        api.RegisterItemClass("ItemSailingTool", typeof(ItemSailingTool));
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<SailingSyncPacket>();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        visuals = new SailingVisuals(api);
        api.Network.GetChannel(ChannelName).SetMessageHandler<SailingSyncPacket>(p => visuals.ApplySync(p));
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
            .EndSubCommand()
            .BeginSubCommand("vane")
                .WithDescription("Read the wind vane: direction, strength and Beaufort force")
                .HandleWith(args => Gated(args.Caller.Player as IServerPlayer, "vane", () => TextCommandResult.Success(Vane(args.Caller.Player as IServerPlayer))))
            .EndSubCommand()
            .BeginSubCommand("anchor")
                .WithDescription("Drop or raise the anchor of the boat you are riding")
                .HandleWith(args => Gated(args.Caller.Player as IServerPlayer, "anchor", () => ToggleAnchor(args.Caller.Player as IServerPlayer)))
            .EndSubCommand()
            .BeginSubCommand("glass")
                .WithDescription("Read the weather glass and wind vane: pressure, trend and forecast")
                .HandleWith(args => Gated(args.Caller.Player as IServerPlayer, "glass", () => TextCommandResult.Success(Glass(args.Caller.Player as IServerPlayer))))
            .EndSubCommand()
            .BeginSubCommand("repair")
                .WithDescription("Repair the hull or sails of a nearby boat using the item in your hand")
                .WithArgs(api.ChatCommands.Parsers.WordRange("part", "hull", "sail"))
                .HandleWith(args => Repair(args.Caller.Player as IServerPlayer, args[0] as string))
            .EndSubCommand()
            .BeginSubCommand("log")
                .WithDescription("Show your sailing logbook")
                .HandleWith(args => Gated(args.Caller.Player as IServerPlayer, "log", () => TextCommandResult.Success(LogBook(args.Caller.Player as IServerPlayer))))
            .EndSubCommand()
            .BeginSubCommand("teststorm")
                .WithDescription("Toggle a simulated temporal storm at sea (testing)")
                .RequiresPrivilege(Privilege.controlserver)
                .HandleWith(args =>
                {
                    forcedStorm = !forcedStorm;
                    return TextCommandResult.Success("Simulated temporal storm " + (forcedStorm ? "ON" : "OFF") + ".");
                })
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

    private Entity? BoatOf(IServerPlayer? player, bool requireMount = true)
    {
        if (player?.Entity == null || (requireMount && player.Entity.MountedOn == null)) return null;
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

    private static double GetCond(Entity boat, string key) => boat.WatchedAttributes.GetDouble(key, 100);

    private static void SetCond(Entity boat, string key, double value)
    {
        value = Math.Clamp(value, 0, 100);
        double old = GetCond(boat, key);
        boat.WatchedAttributes.SetDouble(key, value);
        if (Math.Floor(old) != Math.Floor(value)) boat.WatchedAttributes.MarkPathDirty(key);
    }

    private static bool Matches(string[] prefixes, string path) => prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private TextCommandResult Repair(IServerPlayer? player, string? part)
    {
        Entity? boat = BoatOf(player, requireMount: false);
        if (boat == null || player == null) return TextCommandResult.Error("Stand within 6 blocks of a sailboat.");
        bool hull = part == "hull";
        string key = hull ? HullKey : SailKey;
        string[] accepted = hull ? config.HullRepairItems : config.SailRepairItems;
        ItemSlot? slot = player.InventoryManager.ActiveHotbarSlot;
        string path = slot?.Itemstack?.Collectible?.Code?.Path ?? "";
        if (slot == null || slot.Empty || !Matches(accepted, path))
            return TextCommandResult.Error($"Hold one of these to repair the {(hull ? "hull" : "sails")}: {string.Join(", ", accepted)}.");
        double cur = GetCond(boat, key);
        if (cur >= 99.5) return TextCommandResult.Error($"The {(hull ? "hull" : "sails")} are already in excellent shape.");
        slot.TakeOut(1);
        slot.MarkDirty();
        SetCond(boat, key, cur + SailingModel.RepairPerItem);
        player.Entity.WatchedAttributes.SetInt(LogPrefix + "repairs", player.Entity.WatchedAttributes.GetInt(LogPrefix + "repairs", 0) + 1);
        return TextCommandResult.Success($"Repaired. {(hull ? "Hull" : "Sails")} now {GetCond(boat, key):0}%.");
    }

    private string LogBook(IServerPlayer? player)
    {
        if (player?.Entity == null) return "No logbook.";
        var a = player.Entity.WatchedAttributes;
        return "Logbook\n" +
               $"Distance sailed: {a.GetDouble(LogPrefix + "distance", 0):0} blocks\n" +
               $"Squalls survived: {a.GetInt(LogPrefix + "squalls", 0)}\n" +
               $"Temporal storms survived at sea: {a.GetInt(LogPrefix + "storms", 0)}\n" +
               $"Strongest wind faced: {a.GetDouble(LogPrefix + "topknots", 0):0.#} kn\n" +
               $"Repairs made: {a.GetInt(LogPrefix + "repairs", 0)}";
    }

    private void LogEvent(string name)
    {
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
            if (p.Entity?.MountedOn != null)
                p.Entity.WatchedAttributes.SetInt(LogPrefix + name, p.Entity.WatchedAttributes.GetInt(LogPrefix + name, 0) + 1);
    }

    private void TrackDistance()
    {
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
        {
            if (p.Entity?.MountedOn == null) { lastPlayerPos.Remove(p.PlayerUID); continue; }
            Vec3d pos = p.Entity.Pos.XYZ.Clone();
            if (lastPlayerPos.TryGetValue(p.PlayerUID, out Vec3d? prev))
            {
                double d = Math.Sqrt((pos.X - prev.X) * (pos.X - prev.X) + (pos.Z - prev.Z) * (pos.Z - prev.Z));
                if (d < 50)
                {
                    var a = p.Entity.WatchedAttributes;
                    a.SetDouble(LogPrefix + "distance", a.GetDouble(LogPrefix + "distance", 0) + d);
                }
            }
            lastPlayerPos[p.PlayerUID] = pos;
        }
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

    private static string? ToolOf(ItemSlot slot) => slot?.Itemstack?.Collectible?.Attributes?["sailingTool"]?.AsString();

    private bool HasTool(IServerPlayer player, string tool)
    {
        if (player.WorldData.CurrentGameMode == EnumGameMode.Creative) return true;
        IInventory? hotbar = player.InventoryManager.GetHotbarInventory();
        if (hotbar == null) return false;
        foreach (ItemSlot slot in hotbar)
            if (!slot.Empty && ToolOf(slot) == tool) return true;
        return false;
    }

    private static readonly Dictionary<string, string> ToolNames = new()
    {
        ["glass"] = "weather glass", ["vane"] = "wind vane", ["anchor"] = "anchor", ["log"] = "sailor's logbook"
    };

    /// <summary>Commands remain available, but require the matching item in the hotbar unless RequireItemsForCommands is false.</summary>
    private TextCommandResult Gated(IServerPlayer? player, string tool, Func<TextCommandResult> run)
    {
        if (player != null && config.RequireItemsForCommands && !HasTool(player, tool))
            return TextCommandResult.Error($"You need a {ToolNames[tool]} in your hotbar. Craft one, or ask an admin to enable commands without items.");
        return run();
    }

    /// <summary>Called by the tool items on right-click (server only).</summary>
    public string UseTool(IServerPlayer player, string tool) => tool switch
    {
        "glass" => Glass(player),
        "vane" => Vane(player),
        "anchor" => ToggleAnchor(player).StatusMessage ?? "",
        "log" => LogBook(player),
        _ => ""
    };

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

    private TextCommandResult ToggleAnchor(IServerPlayer? player)
    {
        Entity? boat = BoatOf(player);
        if (boat == null) return TextCommandResult.Error("You must be riding a sailboat.");
        bool down = !boat.WatchedAttributes.GetBool(AnchorKey, false);
        boat.WatchedAttributes.SetBool(AnchorKey, down);
        boat.WatchedAttributes.MarkPathDirty(AnchorKey);
        return TextCommandResult.Success(down
            ? $"Anchor dropped. It holds against about {config.AnchorHoldKnots:0} kn of wind."
            : "Anchor raised. You are free to sail.");
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
                sapi!.Logger.Warning("[NewfiesSailing] Could not read temporal storm state from SystemTemporalStability; use /sailing teststorm to test.");
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
        if (temporalStorm) SpawnBoarder();
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
        return speed * config.KnotsPerWindUnit * squallFactor * seasonFactor * (temporalStorm ? config.TemporalStormWindBoost : 1);
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
        UpdateTemporalStorm();
        TrackDistance();
        if (elapsed - lastSyncAt >= 1)
        {
            lastSyncAt = elapsed;
            serverChannel?.BroadcastPacket(new SailingSyncPacket { Squall = squallFactor, TemporalStorm = temporalStorm, Seconds = elapsed, WindRotationAmplitude = config.TemporalStormWindRotation, WindBoost = config.TemporalStormWindBoost, SeasonFactor = seasonFactor });
        }

        long now = Environment.TickCount64;
        foreach (Entity boat in boats.Values.ToArray())
        {
            if (!boat.Alive) continue;
            if (boat.WatchedAttributes.GetBool(AnchorKey, false))
            {
                HoldAnchor(boat, now);
                continue;
            }
            if (!HasMountedPlayerNear(boat)) continue;
            var motion = boat.SidedPos.Motion;
            double speed = Math.Sqrt(motion.X * motion.X + motion.Z * motion.Z);
            if (speed < config.MinMotion) continue;

            double area = SailingModel.SailArea(GetTrim(boat));
            double knots = KnotsAt(boat.SidedPos.XYZ, out Vec3d wind) * SailingModel.Gust(elapsed, config.GustAmplitude * (temporalStorm ? 2 : 1), boat.EntityId % 100);
            double off = SailingModel.AngleOffWind(motion.X, motion.Z, wind.X, wind.Z);
            double target = SailingModel.SpeedMultiplier(knots, off, config.MinSpeedMultiplier, config.MaxSpeedMultiplier, config.ReferenceKnots, config.OverloadKnots, area);
            if (config.WearEnabled)
            {
                int force = SailingModel.StormForce(SailingModel.Beaufort(knots), temporalStorm);
                var (hullWear, sailWear) = SailingModel.WearPerSecond(force, SailingModel.Overpowered(knots, area, config.OverloadKnots), temporalStorm, area);
                double hullBefore = GetCond(boat, HullKey), sailBefore = GetCond(boat, SailKey);
                double hullNow = hullBefore - hullWear * config.WearRate * dt, sailNow = sailBefore - sailWear * config.WearRate * dt;
                SetCond(boat, HullKey, hullNow);
                SetCond(boat, SailKey, sailNow);
                WarnCondition(boat, "hull", hullBefore, hullNow);
                WarnCondition(boat, "sails", sailBefore, sailNow);
                target *= SailingModel.ConditionSpeedFactor(GetCond(boat, HullKey), GetCond(boat, SailKey));
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

    private void WarnCondition(Entity boat, string part, double before, double after)
    {
        foreach (double threshold in new[] { 50.0, 25.0, 10.0 })
        {
            if (before > threshold && after <= threshold)
            {
                NotifySailors(part == "hull"
                    ? $"[Sailing] The hull is at {threshold:0}% and taking on water! Repair with planks: /sailing repair hull"
                    : $"[Sailing] The sails are at {threshold:0}% and in tatters! Repair with linen or rope: /sailing repair sail");
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
            NotifySailors("[Sailing] The anchor is dragging in this wind!");
        }
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
        if (temporalStorm) text += "\nTEMPORAL STORM: compass unreliable, wind direction wandering, seas unnaturally rough.";
        if (squallFactor > 1.05) text += "\nA squall is passing: wind is up " + $"{(squallFactor - 1) * 100:0}%.";
        Entity? boat = BoatOf(player);
        if (boat != null)
        {
            SailTrim trim = GetTrim(boat);
            text += $"\nSails: {trim.ToString().ToLowerInvariant()}." + (boat.WatchedAttributes.GetBool(AnchorKey, false) ? " Anchor down." : "");
            if (SailingModel.Overpowered(knots, SailingModel.SailArea(trim), config.OverloadKnots)) text += " Overpowered - reef!";
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
