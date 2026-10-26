using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Admin;

/// <summary>
/// Pure rules for <c>/disguise</c>, kept free of the game API so they can be tested.
/// </summary>
public static class DisguiseRules
{
    public const string Marker = "nsDisguise";
    public const string OwnerKey = "nsDisguiseOwner";
    /// <summary>Disguise entities alive at once. Each is a real world entity, so this bounds the cost of misuse.</summary>
    public const int MaximumConcurrent = 4;
    /// <summary>Minimum time between disguise changes by one admin.</summary>
    public const int CooldownMs = 2000;

    /// <summary>"cow" becomes "game:cow"; a code that already has a domain is kept. Returns "" for unusable input.</summary>
    public static string NormalizeCode(string? input)
    {
        string text = (input ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0 || text.Length > 100 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "";
        if (text.Count(c => c == ':') > 1 || text.StartsWith(':') || text.EndsWith(':')) return "";
        return text.Contains(':') ? text : "game:" + text;
    }

    /// <summary>Animation to play for a horizontal speed in blocks per second.</summary>
    public static string PickAnimation(double blocksPerSecond) =>
        blocksPerSecond < 0.4 ? "idle" : blocksPerSecond < 3.5 ? "walk" : "run";

    /// <summary>Real players are never a valid disguise.</summary>
    public static bool IsForbidden(string code) =>
        code.EndsWith(":player", StringComparison.Ordinal) || code.Contains("player-", StringComparison.Ordinal);

    /// <summary>Case-insensitive search over entity codes, best matches (shortest) first.</summary>
    public static List<string> Search(IEnumerable<string> codes, string filter, int limit)
    {
        string f = (filter ?? "").Trim().ToLowerInvariant();
        return codes.Where(c => !IsForbidden(c) && (f.Length == 0 || c.ToLowerInvariant().Contains(f)))
            .OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).Take(Math.Max(1, limit)).ToList();
    }

    /// <summary>
    /// Resolves what an admin typed against the registered codes: an exact code wins, otherwise a single unique
    /// partial match is accepted. Anything ambiguous is reported instead of guessed.
    /// </summary>
    public static string Resolve(IReadOnlyCollection<string> registered, string input, out List<string> candidates)
    {
        candidates = new List<string>();
        string normalized = NormalizeCode(input);
        if (normalized.Length > 0 && registered.Contains(normalized, StringComparer.OrdinalIgnoreCase) && !IsForbidden(normalized))
            return normalized;
        candidates = Search(registered, (input ?? "").Trim(), 12);
        return candidates.Count == 1 ? candidates[0] : "";
    }
}

/// <summary>
/// Admin-only disguise. Vintage Story cannot re-skin another player's model from the server, so the disguise is a real
/// entity that follows the admin's position and facing every few ticks while the admin is hidden (suite ghost mode).
/// Consequences, by design: the admin is in spectator mode while disguised, and the entity is a normal world entity
/// (it can be hit, and it is removed again on disguise-off, disconnect, shutdown and the next start after a crash).
/// </summary>
public sealed class DisguiseService : IDisposable
{
    private const int FollowIntervalMs = 50;
    private const int MaximumNameLength = 100;

    private readonly ICoreServerAPI api;
    private readonly AdminService admin;
    private readonly object gate = new();
    private readonly Dictionary<string, State> states = new(StringComparer.OrdinalIgnoreCase);
    private long tickListener = -1;
    private bool disposed;
    private readonly Dictionary<string, long> lastChangeAt = new(StringComparer.OrdinalIgnoreCase);

    private sealed class State
    {
        public string EntityCode = "";
        public long EntityId;
        public bool HiddenByDisguise;
        public int Respawns;
        public double LastX, LastZ;
        public long LastAtMs;
        public bool HasLast;
        public string Animation = "";
    }

    public DisguiseService(ICoreServerAPI api, AdminService admin)
    {
        this.api = api;
        this.admin = admin;
        api.Event.OnEntitySpawn += OnEntitySpawn;
        api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
        // Entities saved with the world after a crash are removed once the world is running.
        api.Event.RegisterCallback(_ => RemoveStrayDisguises(), 20_000);
    }

    public bool IsDisguised(string playerUid)
    {
        lock (gate) return states.ContainsKey(playerUid);
    }

    public string Describe(string playerUid)
    {
        lock (gate) return states.TryGetValue(playerUid, out State? s) ? s.EntityCode : "";
    }

    /// <summary>Registered entity codes, read by name so a changed API surface degrades to "no list" rather than a crash.</summary>
    public List<string> RegisteredCodes()
    {
        var codes = new List<string>();
        try
        {
            var types = api.World.GetType().GetProperty("EntityTypes")?.GetValue(api.World) as IEnumerable;
            if (types == null) return codes;
            foreach (object? type in types)
            {
                object? code = type?.GetType().GetProperty("Code")?.GetValue(type) ?? type?.GetType().GetField("Code")?.GetValue(type);
                string text = code?.ToString() ?? "";
                if (text.Length > 0 && text.Length <= MaximumNameLength) codes.Add(text);
            }
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/Disguise] entity type list unavailable: {0}", ex.Message); }
        return codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string Enable(IServerPlayer player, string input, bool hidePlayer)
    {
        if (disposed || player?.Entity == null || string.IsNullOrWhiteSpace(player.PlayerUID))
            return "Disguise is unavailable for this player.";

        long now = Environment.TickCount64;
        lock (gate)
        {
            if (lastChangeAt.TryGetValue(player.PlayerUID, out long previousChange) && now - previousChange < DisguiseRules.CooldownMs)
                return "Wait a moment before changing your disguise again.";
            if (!states.ContainsKey(player.PlayerUID) && states.Count >= DisguiseRules.MaximumConcurrent)
                return $"{DisguiseRules.MaximumConcurrent} disguises are already active. Ask another admin to use /disguise off first.";
            lastChangeAt[player.PlayerUID] = now;
        }

        var registered = RegisteredCodes();
        string code;
        if (registered.Count > 0)
        {
            code = DisguiseRules.Resolve(registered, input, out List<string> candidates);
            if (code.Length == 0)
                return candidates.Count == 0
                    ? $"No entity type matches '{input}'. Try /disguise list <part of a name>."
                    : "Several entity types match, be more specific: " + string.Join(", ", candidates.Take(12));
        }
        else
        {
            // The type list could not be read; fall back to asking the game for the exact code.
            code = DisguiseRules.NormalizeCode(input);
            if (code.Length == 0 || DisguiseRules.IsForbidden(code)) return "That is not a valid entity code. Example: /disguise game:chicken-hen";
        }

        if (!EntityPositionResolver.TryGetPose(player.Entity, out double x, out double y, out double z, out int dimension, out float yaw, out float pitch))
            return "Your position could not be read, so the disguise was not created.";

        // Switching disguise: remove the old entity first, keep the hidden state.
        bool wasHidden = false;
        lock (gate)
            if (states.TryGetValue(player.PlayerUID, out State? previous)) wasHidden = previous.HiddenByDisguise;
        RemoveEntityFor(player.PlayerUID, keepState: true);

        // Pitch is the admin's head tilt. On a mob it would tilt the whole body, so the disguise always stands level.
        Entity? entity = Spawn(code, x, y, z, yaw, 0f, player.PlayerUID, out string error);
        if (entity == null)
        {
            lock (gate) states.Remove(player.PlayerUID);
            return error;
        }

        bool hidden = wasHidden;
        if (hidePlayer && !admin.IsGhosted(player.PlayerUID))
        {
            // Reuses suite ghost mode: spectator, free move, and the client-side hide when supported.
            admin.ToggleGhost(player, "disguise");
            hidden = true;
        }

        lock (gate) states[player.PlayerUID] = new State { EntityCode = code, EntityId = entity.EntityId, HiddenByDisguise = hidden };
        EnsureTickListener();
        admin.Audit(player.PlayerName, "disguise-on", player.PlayerUID, player.PlayerName, "Disguised as " + code, "ingame");
        string note = hidden ? DescribeHideSupport(player) : "";
        return $"You are disguised as {code}." + (hidden ? " You are hidden in ghost mode (spectator), so you cannot interact until you use /disguise off." : "") +
               note + " The entity follows your position; other players cannot hit or use it. Use /disguise off to remove it.";
    }

    /// <summary>
    /// Ghost hiding needs a client that advertises support. Say how many online players may still see the real model,
    /// so the admin knows whether the disguise will hold up in front of them.
    /// </summary>
    private string DescribeHideSupport(IServerPlayer self)
    {
        try
        {
            int unsupported = api.World.AllOnlinePlayers.OfType<IServerPlayer>()
                .Count(p => !string.Equals(p.PlayerUID, self.PlayerUID, StringComparison.OrdinalIgnoreCase) &&
                            !BlockLogger.AntiCheat.GhostVisibilityCompatibilityRegistry.IsSafe(p.PlayerUID));
            return unsupported == 0 ? "" : $" {unsupported} online player(s) use a client without ghost-hide support and may still see your real model next to the disguise.";
        }
        catch { return ""; }
    }

    public string Disable(IServerPlayer player, string source = "ingame")
    {
        if (player == null) return "Disguise is unavailable.";
        bool had;
        bool restoreGhost = false;
        lock (gate)
        {
            had = states.TryGetValue(player.PlayerUID, out State? state);
            restoreGhost = had && state!.HiddenByDisguise;
        }
        if (!had) return "You are not disguised.";
        RemoveEntityFor(player.PlayerUID, keepState: false);
        if (restoreGhost && admin.IsGhosted(player.PlayerUID)) admin.ToggleGhost(player, "disguise");
        admin.Audit(player.PlayerName, "disguise-off", player.PlayerUID, player.PlayerName, "Disguise removed", source);
        return "Disguise removed." + (restoreGhost ? " Your previous game mode was restored." : "");
    }

    public void OnPlayerDisconnect(IServerPlayer player)
    {
        if (player == null) return;
        // AdminService restores ghost state itself on disconnect; here only the entity needs to go.
        RemoveEntityFor(player.PlayerUID, keepState: false);
        lock (gate) lastChangeAt.Remove(player.PlayerUID);
    }

    private Entity? Spawn(string code, double x, double y, double z, float yaw, float pitch, string ownerUid, out string error)
    {
        error = "";
        try
        {
            EntityProperties? props = api.World.GetEntityType(new AssetLocation(code));
            if (props == null) { error = "That entity type is not registered: " + code; return null; }
            Entity entity = api.World.ClassRegistry.CreateEntity(props);
            if (!EntityPositionResolver.TrySetPose(entity, x, y, z, yaw, pitch)) { error = "The entity could not be positioned."; return null; }
            entity.WatchedAttributes.SetBool(DisguiseRules.Marker, true);
            entity.WatchedAttributes.SetString(DisguiseRules.OwnerKey, ownerUid);
            api.World.SpawnEntity(entity);
            StopWandering(entity);
            return entity;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/Disguise] could not spawn {0}: {1}: {2}", code, ex.GetType().Name, ex.Message);
            error = "The disguise could not be created (" + ex.GetType().Name + "). See the server log.";
            return null;
        }
    }

    /// <summary>
    /// The follow tick resets the pose every 100 ms, which already pins the entity. Where the game exposes the AI
    /// behaviors, also remove them so a disguise cannot wander, flee or attack between ticks. Optional and by name.
    /// </summary>
    private void StopWandering(Entity entity)
    {
        try
        {
            object? sided = entity.GetType().GetProperty("SidedProperties")?.GetValue(entity);
            var behaviors = sided?.GetType().GetField("Behaviors")?.GetValue(sided) as IList;
            var remove = entity.GetType().GetMethod("RemoveBehavior");
            if (behaviors == null || remove == null) return;
            foreach (object? behavior in behaviors.Cast<object?>().ToArray())
            {
                string name = behavior?.GetType().GetMethod("PropertyName")?.Invoke(behavior, null) as string ?? "";
                if (name.Contains("taskai", StringComparison.OrdinalIgnoreCase) || name.Contains("pathfind", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("aggro", StringComparison.OrdinalIgnoreCase) || name.Contains("emotionstate", StringComparison.OrdinalIgnoreCase))
                    remove.Invoke(entity, new[] { behavior });
            }
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/Disguise] AI behaviors left in place: {0}", ex.Message); }
    }

    private void RemoveEntityFor(string uid, bool keepState)
    {
        long entityId = 0;
        lock (gate)
        {
            if (states.TryGetValue(uid, out State? state)) entityId = state.EntityId;
            if (!keepState) states.Remove(uid);
        }
        RemoveEntity(entityId);
        if (!keepState) StopTickListenerIfIdle();
    }

    private void RemoveEntity(long entityId)
    {
        if (entityId == 0) return;
        try
        {
            if (api.World.LoadedEntities.TryGetValue(entityId, out Entity? entity) && entity != null)
                entity.Die(EnumDespawnReason.Removed);
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/Disguise] removing entity {0}: {1}", entityId, ex.Message); }
    }

    private void EnsureTickListener()
    {
        lock (gate)
            if (tickListener < 0 && !disposed)
                tickListener = api.Event.RegisterGameTickListener(_ => Follow(), FollowIntervalMs);
    }

    private void StopTickListenerIfIdle()
    {
        lock (gate)
        {
            if (states.Count > 0 || tickListener < 0) return;
            try { api.Event.UnregisterGameTickListener(tickListener); } catch { }
            tickListener = -1;
        }
    }

    private void Follow()
    {
        if (disposed) return;
        List<KeyValuePair<string, State>> snapshot;
        lock (gate) snapshot = states.ToList();
        foreach (var pair in snapshot)
        {
            try
            {
                IServerPlayer? owner = api.World.AllOnlinePlayers.OfType<IServerPlayer>()
                    .FirstOrDefault(p => string.Equals(p.PlayerUID, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (owner?.Entity == null) { RemoveEntityFor(pair.Key, keepState: false); continue; }
                if (!EntityPositionResolver.TryGetPose(owner.Entity, out double x, out double y, out double z, out _, out float yaw, out float pitch)) continue;

                Entity? disguise = null;
                if (api.World.LoadedEntities.TryGetValue(pair.Value.EntityId, out Entity? found) && found != null && found.Alive) disguise = found;
                if (disguise == null)
                {
                    // Killed or unloaded. Bring it back, but stop after a few failures instead of looping forever.
                    if (pair.Value.Respawns >= 5)
                    {
                        admin.Audit(owner.PlayerName, "disguise-off", owner.PlayerUID, owner.PlayerName, "Disguise entity kept disappearing; removed", "ingame");
                        Disable(owner, "auto");
                        continue;
                    }
                    pair.Value.Respawns++;
                    disguise = Spawn(pair.Value.EntityCode, x, y, z, yaw, 0f, owner.PlayerUID, out _);
                    if (disguise == null) continue;
                    pair.Value.EntityId = disguise.EntityId;
                    pair.Value.Animation = ""; // a new entity has no animation running yet
                }
                EntityPositionResolver.TrySetPose(disguise, x, y, z, yaw, 0f);
                MatchAnimation(disguise, pair.Value, x, z);
            }
            catch (Exception ex) { api.Logger.Debug("[ServerEssentials/Disguise] follow tick: {0}", ex.Message); }
        }
    }

    /// <summary>
    /// Plays idle, walk or run on the disguise from how fast the admin is moving. Animation codes differ per entity, so
    /// an unknown code simply does nothing; the call is made by name so a changed animation API cannot break the Suite.
    /// </summary>
    private void MatchAnimation(Entity entity, State state, double x, double z)
    {
        long now = Environment.TickCount64;
        double speed = 0;
        if (state.HasLast && now > state.LastAtMs)
        {
            double dx = x - state.LastX, dz = z - state.LastZ;
            speed = Math.Sqrt(dx * dx + dz * dz) / ((now - state.LastAtMs) / 1000.0);
        }
        state.LastX = x; state.LastZ = z; state.LastAtMs = now; state.HasLast = true;

        string wanted = DisguiseRules.PickAnimation(speed);
        if (wanted == state.Animation) return;
        try
        {
            object? animations = entity.GetType().GetProperty("AnimManager")?.GetValue(entity);
            if (animations == null) return;
            var stringArgument = new[] { typeof(string) };
            if (state.Animation.Length > 0)
                animations.GetType().GetMethod("StopAnimation", stringArgument)?.Invoke(animations, new object[] { state.Animation });
            animations.GetType().GetMethod("StartAnimation", stringArgument)?.Invoke(animations, new object[] { wanted });
            state.Animation = wanted;
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/Disguise] animation {0}: {1}", wanted, ex.Message); state.Animation = wanted; }
    }

    /// <summary>Nobody else can hit, mount or otherwise use a disguise entity.</summary>
    private void OnPlayerInteractEntity(Entity entity, IPlayer byPlayer, ItemSlot slot, Vec3d hitPosition, int mode, ref EnumHandling handling)
    {
        try
        {
            if (entity != null && entity.WatchedAttributes.GetBool(DisguiseRules.Marker, false)) handling = EnumHandling.PreventDefault;
        }
        catch { }
    }

    private void OnEntitySpawn(Entity entity)
    {
        try
        {
            if (entity == null || !entity.WatchedAttributes.GetBool(DisguiseRules.Marker, false)) return;
            string owner = entity.WatchedAttributes.GetString(DisguiseRules.OwnerKey, "");
            lock (gate)
                if (owner.Length > 0 && states.TryGetValue(owner, out State? state) && state.EntityId == entity.EntityId) return;
            // A marked entity nobody owns (saved with the world, or its owner left) must not stay.
            api.Event.RegisterCallback(_ => RemoveEntity(entity.EntityId), 500);
        }
        catch { }
    }

    private void RemoveStrayDisguises()
    {
        if (disposed) return;
        try
        {
            HashSet<long> owned;
            lock (gate) owned = new HashSet<long>(states.Values.Select(s => s.EntityId));
            foreach (Entity entity in api.World.LoadedEntities.Values.ToArray())
                if (entity != null && !owned.Contains(entity.EntityId) && entity.WatchedAttributes.GetBool(DisguiseRules.Marker, false))
                    entity.Die(EnumDespawnReason.Removed);
        }
        catch (Exception ex) { api.Logger.Debug("[ServerEssentials/Disguise] stray sweep: {0}", ex.Message); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { api.Event.OnEntitySpawn -= OnEntitySpawn; } catch { }
        try { api.Event.OnPlayerInteractEntity -= OnPlayerInteractEntity; } catch { }
        long[] ids;
        lock (gate)
        {
            ids = states.Values.Select(s => s.EntityId).ToArray();
            states.Clear();
            if (tickListener >= 0) { try { api.Event.UnregisterGameTickListener(tickListener); } catch { } tickListener = -1; }
        }
        foreach (long id in ids) RemoveEntity(id);
    }
}
