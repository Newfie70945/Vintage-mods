using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NewfiesSailing;

/// <summary>Tool items: weather glass, vane, compass, horn, shanty, repair, upgrades, charts, towing and rowing.</summary>
public sealed partial class SailingModSystem
{
    private void Play(string name, Vec3d pos, float range = 40, float volume = 1f)
    {
        try { sapi!.World.PlaySoundAt(new AssetLocation("newfiessailing:sounds/" + name), pos.X, pos.Y, pos.Z, null, true, range, volume); }
        catch (Exception ex) { sapi!.Logger.Debug("[NewfiesSailing] Sound failed: " + ex.Message); }
    }

    /// <summary>Fog horn: audible far across the water; every sailor in range is told roughly which way it came from.</summary>
    private (string Text, bool Used) Horn(IServerPlayer player)
    {
        if (lastHorn.TryGetValue(player.PlayerUID, out double at) && elapsed - at < 8) return ("The horn needs a moment to recover its breath.", false);
        lastHorn[player.PlayerUID] = elapsed;
        Vec3d pos = player.Entity.Pos.XYZ;
        Play("foghorn", pos, 120, 1f);
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
        {
            if (p.PlayerUID == player.PlayerUID || p is not IServerPlayer sp || p.Entity?.MountedOn == null) continue;
            double dx = pos.X - p.Entity.Pos.X, dz = pos.Z - p.Entity.Pos.Z, dist = Math.Sqrt(dx * dx + dz * dz);
            if (dist > 250) continue;
            double bearing = (Math.Atan2(dx, -dz) * 180.0 / Math.PI + 360) % 360;
            sp.SendMessage(GlobalConstants.InfoLogChatGroup, $"[Sailing] A fog horn sounds to the {SailingModel.Compass(bearing)}, {(dist < 60 ? "very close" : dist < 150 ? "not far off" : "far away")}.", EnumChatType.Notification);
        }
        return ("You sound the fog horn: a long, low blast rolls out over the water.", false);
    }

    /// <summary>Concertina: plays a sea shanty and lifts the morale of the boat's crew within 10 blocks.</summary>
    private (string Text, bool Used) Shanty(IServerPlayer player)
    {
        Play("shanty", player.Entity.Pos.XYZ, 40, 1f);
        Entity? boat = BoatOf(player, requireMount: false, radius: 10);
        if (boat == null) return ("You squeeze out a shanty, but there is no boat's crew to hear it.", false);
        if (lastShanty.TryGetValue(boat.EntityId, out double at) && elapsed - at < 60) return ("The crew has heard enough shanties for a minute.", false);
        lastShanty[boat.EntityId] = elapsed;
        SetMorale(boat, GetMorale(boat) + 20);
        return ($"The crew joins in on the chorus! Morale {GetMorale(boat):0}% ({SailingModel.MoraleLabel(GetMorale(boat))}).", false);
    }

    private (string Text, bool Used) NameBoat(IServerPlayer player)
    {
        Entity? boat = BoatOf(player, requireMount: false);
        if (boat == null) return ("Stand within 6 blocks of a sailboat to name it.", false);
        string name = SailingModel.BoatName(rng.Next());
        boat.WatchedAttributes.SetString(NameKey, name);
        boat.WatchedAttributes.MarkPathDirty(NameKey);
        return ($"A brass nameplate is screwed to the transom: \"{name}\". Fair winds to her!", true);
    }

    private (string Text, bool Used) Upgrade(IServerPlayer player, int flag, string what)
    {
        Entity? boat = BoatOf(player, requireMount: false);
        if (boat == null) return ("Stand within 6 blocks of a sailboat to fit it.", false);
        int flags = GetUpgrades(boat);
        if ((flags & flag) != 0) return ($"{Nm(boat)} already has a {what}.", false);
        boat.WatchedAttributes.SetInt(UpgradeKey, flags | flag);
        boat.WatchedAttributes.MarkPathDirty(UpgradeKey);
        return ($"You fit a {what} to {Nm(boat).ToLowerInvariant()}.", true);
    }

    /// <summary>Gives the player a treasure chart marking a random spot 150-400 blocks away. Returns whether it was given.</summary>
    public bool GiveChart(IServerPlayer player)
    {
        try
        {
            Item? item = sapi!.World.GetItem(new AssetLocation("newfiessailing:treasurechart"));
            if (item == null || player.Entity == null) return false;
            double angle = rng.NextDouble() * Math.PI * 2, dist = 150 + rng.NextDouble() * 250;
            var stack = new ItemStack(item);
            stack.Attributes.SetInt("tx", (int)(player.Entity.Pos.X + Math.Cos(angle) * dist));
            stack.Attributes.SetInt("tz", (int)(player.Entity.Pos.Z + Math.Sin(angle) * dist));
            if (!player.InventoryManager.TryGiveItemstack(stack, true)) sapi.World.SpawnItemEntity(stack, player.Entity.Pos.XYZ);
            return true;
        }
        catch (Exception ex) { sapi!.Logger.Warning("[NewfiesSailing] Could not give chart: " + ex.Message); return false; }
    }

    /// <summary>Treasure chart: reports the direction and distance to the mark; digs the treasure up when you stand on it.</summary>
    public (string Text, bool Used) UseChart(IServerPlayer player, ItemSlot slot)
    {
        var attrs = slot.Itemstack?.Attributes;
        if (attrs == null || !attrs.HasAttribute("tx")) return ("The chart is blank - just waves and a smudge.", false);
        double dx = attrs.GetInt("tx") - player.Entity.Pos.X, dz = attrs.GetInt("tz") - player.Entity.Pos.Z;
        string hint = SailingModel.ChartHint(dx, dz);
        if (dx * dx + dz * dz > 36) return (hint, false);
        int found = SailingLoot.Spawn(sapi!.World, TreasureLoot, player.Entity.Pos.XYZ.AddCopy(0, 0.5, 0));
        var a = player.Entity.WatchedAttributes;
        a.SetInt(LogPrefix + "treasures", a.GetInt(LogPrefix + "treasures", 0) + 1);
        return (found > 0 ? "You dig... and strike a rotten chest! Coins and metal spill out." : "You dig... but someone got here first. Only a rusted hinge remains.", true);
    }

    /// <summary>Oars: each stroke tick pushes the boat the rower is riding along their view direction, up to a rowing speed limit.</summary>
    public void Row(IServerPlayer player)
    {
        if (!config.RowingEnabled) return;
        Entity? boat = BoatOf(player);
        if (boat == null || IsCapsized(boat) || boat.WatchedAttributes.GetBool(AnchorKey, false)) return;
        var look = player.Entity.SidedPos.GetViewVector();
        double len = Math.Sqrt(look.X * look.X + look.Z * look.Z);
        if (len < 1e-6) return;
        var m = boat.SidedPos.Motion;
        double push = SailingModel.RowPush(Math.Sqrt(m.X * m.X + m.Z * m.Z), config.RowPush, config.RowMaxSpeed);
        m.X += look.X / len * push;
        m.Z += look.Z / len * push;
    }

    /// <summary>
    /// Tow rope: first use makes the rope fast to the nearest boat; the second use, beside a different boat, hitches the first
    /// to it. Using it again beside a towed or towing boat casts the rope off.
    /// </summary>
    private (string Text, bool Used) Tow(IServerPlayer player)
    {
        if (!config.TowingEnabled) return ("Towing is disabled on this server.", false);
        Entity? boat = BoatOf(player, requireMount: false, radius: 10);
        if (boat == null) return ("No sailboat within 10 blocks.", false);

        if (tows.ContainsKey(boat.EntityId) || tows.ContainsValue(boat.EntityId))
        {
            foreach (long key in tows.Where(kv => kv.Key == boat.EntityId || kv.Value == boat.EntityId).Select(kv => kv.Key).ToList()) tows.Remove(key);
            pendingTow.Remove(player.PlayerUID);
            return ($"You cast off the tow rope at {Nm(boat).ToLowerInvariant()}.", false);
        }
        if (!pendingTow.TryGetValue(player.PlayerUID, out long first))
        {
            pendingTow[player.PlayerUID] = boat.EntityId;
            return ($"The rope is made fast to {Nm(boat).ToLowerInvariant()}. Go to the towing boat and use the rope again.", false);
        }
        pendingTow.Remove(player.PlayerUID);
        if (first == boat.EntityId) return ("That is the same boat - pick the boat that will do the towing.", false);
        if (!boats.ContainsKey(first)) return ("The first boat is no longer here.", false);
        tows[first] = boat.EntityId;
        return ($"{Nm(boats[first])} is now under tow behind {Nm(boat).ToLowerInvariant()}.", false);
    }

    private void ApplyTows()
    {
        if (tows.Count == 0) return;
        bool particles = elapsed - lastTowParticles >= 1;
        if (particles) lastTowParticles = elapsed;
        foreach (var (towedId, towerId) in tows.ToList())
        {
            if (!boats.TryGetValue(towedId, out Entity? towed) || !boats.TryGetValue(towerId, out Entity? tower) || !towed.Alive || !tower.Alive)
            {
                tows.Remove(towedId);
                continue;
            }
            double dx = tower.ServerPos.X - towed.ServerPos.X, dz = tower.ServerPos.Z - towed.ServerPos.Z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d > config.TowBreakDistance)
            {
                tows.Remove(towedId);
                NotifyNear(towed, "[Sailing] The tow rope parts with a crack!");
                continue;
            }
            double pull = SailingModel.TowPull(d, config.TowSlack, 0.0004, 0.01);
            if (pull > 0 && d > 1e-6)
            {
                towed.ServerPos.Motion.X += dx / d * pull;
                towed.ServerPos.Motion.Z += dz / d * pull;
            }
            if (particles) RopeParticles(towed.ServerPos.XYZ, tower.ServerPos.XYZ);
        }
    }

    private void RopeParticles(Vec3d a, Vec3d b)
    {
        for (int i = 1; i <= 5; i++)
        {
            double t = i / 6.0;
            double x = a.X + (b.X - a.X) * t, y = a.Y + 0.9 - 0.25 * Math.Sin(t * Math.PI) + (b.Y - a.Y) * t, z = a.Z + (b.Z - a.Z) * t;
            sapi!.World.SpawnParticles(new SimpleParticleProperties(
                1, 1, ColorUtil.ToRgba(255, 190, 160, 110),
                new Vec3d(x, y, z), new Vec3d(x + 0.05, y + 0.05, z + 0.05),
                new Vec3f(0, 0, 0), new Vec3f(0, 0, 0), 1.4f, 0f, 0.15f, 0.15f, EnumParticleModel.Cube));
        }
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
               $"Repairs made: {a.GetInt(LogPrefix + "repairs", 0)}\n" +
               $"Capsizes: {a.GetInt(LogPrefix + "capsizes", 0)}, boats rescued: {a.GetInt(LogPrefix + "rescues", 0)}\n" +
               $"Wrecks salvaged: {a.GetInt(LogPrefix + "wrecks", 0)}\n" +
               $"Treasures dug up: {a.GetInt(LogPrefix + "treasures", 0)}";
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

    /// <summary>Called by the tool items on right-click (server only). Used = a durability use was spent.</summary>
    public (string Text, bool Used) UseTool(IServerPlayer player, string tool) => tool switch
    {
        "glass" => (Glass(player), false),
        "vane" => (Vane(player), false),
        "compass" => (Report(player), false),
        "anchor" => (ToggleAnchor(player), false),
        "trim" => (CycleTrim(player), false),
        "repair" => RepairBoat(player),
        "balloon" => Balloon(player),
        "nameplate" => NameBoat(player),
        "keel" => Upgrade(player, SailingModel.UpgradeKeel, "deep keel"),
        "cloth" => Upgrade(player, SailingModel.UpgradeSailcloth, "set of heavy sailcloth"),
        "reinforce" => Upgrade(player, SailingModel.UpgradeReinforcement, "reinforced hull"),
        "rod" => Upgrade(player, SailingModel.UpgradeLightningRod, "lightning rod"),
        "tow" => Tow(player),
        "horn" => Horn(player),
        "shanty" => Shanty(player),
        "bail" => Bail(player),
        "log" => (LogBook(player), false),
        _ => ("", false)
    };
}
