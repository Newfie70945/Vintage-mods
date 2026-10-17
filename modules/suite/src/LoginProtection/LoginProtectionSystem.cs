using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BlockLogger.LoginProtection;
using BlockLogger.Config;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.LoginProtection;

/// <summary>
/// Server-side join/respawn protection integrated into the utility mod.
///
/// Protection state is authoritative by PlayerUID rather than entity id because
/// the player entity can be replaced on respawn. No repeating protection tick is
/// used: each activation schedules one expiry callback.
/// </summary>
public sealed class LoginProtectionSystem : ModSystem
{
    private const string ConfigFile = "NewfiesLoginProtection.json";
    private const string LegacyConfigFile = "loginprotection.json";

    public static LoginProtectionSystem? Current { get; private set; }

    private ICoreServerAPI sapi = null!;
    private LoginProtectionConfig config = new();
    private readonly Dictionary<string, ProtectionState> protectedPlayers =
        new(StringComparer.Ordinal);
    private long protectionGeneration;
    private readonly Dictionary<string, RebindState> pendingRebinds = new(StringComparer.Ordinal);
    private long rebindListenerId = -1;
    private const int RebindIntervalMs = 250;
    private const int RebindWindowMs = 5000;
    private EventInfo? playerReadyEventInfo;
    private Delegate? playerReadyHandler;
    private bool playerReadyHooked;
    private bool playerNowPlayingFallbackHooked;
    private bool playerJoinHooked;
    private bool entityLifecycleHooked;
    private readonly HashSet<string> readyForActivation = new(StringComparer.Ordinal);
    private long protectionGuardListenerId = -1;
    private const int PlayerReadyFallbackDelayMs = 1500;
    private const int ProtectionGuardIntervalMs = 250;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public LoginProtectionConfig Config => config;

    public int ActiveProtectedCount
    {
        get
        {
            PruneExpiredProtection();
            return protectedPlayers.Values.Count(x =>
                x.Phase == ProtectionPhase.Active &&
                x.EndsUtcTicks > DateTime.UtcNow.Ticks);
        }
    }

    public int PendingProtectedCount
    {
        get
        {
            PruneExpiredProtection();
            return protectedPlayers.Values.Count(x =>
                x.Phase == ProtectionPhase.Waiting ||
                x.Phase == ProtectionPhase.Armed);
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        if (!SuiteFeatureConfig.Load(api).LoginProtection)
        {
            api.Logger.Notification("[LoginProtection] Entire subsystem disabled by {0}; no join/respawn hooks or command registered.", SuiteFeatureConfig.ConfigFile);
            return;
        }

        Current = this;

        LoadConfig();

        playerReadyHooked = TryHookPlayerReady();

        // PlayerJoin fires while the client is still loading. Only arm a pending shield there;
        // the full countdown begins after the player is actually ready and the damage behavior
        // is bound to the live entity.
        sapi.Event.PlayerJoin += OnPlayerJoin;
        playerJoinHooked = true;
        sapi.Event.PlayerNowPlaying += OnPlayerNowPlayingFallback;
        playerNowPlayingFallbackHooked = true;
        sapi.Event.OnEntitySpawn += OnPlayerEntityLifecycle;
        sapi.Event.OnEntityLoaded += OnPlayerEntityLifecycle;
        entityLifecycleHooked = true;
        if (!playerReadyHooked)
        {
            sapi.Logger.Warning(
                "[LoginProtection] PlayerReady is unavailable in the loaded Vintage Story API; PlayerJoin + delayed PlayerNowPlaying reliability mode is active.");
        }
        sapi.Event.PlayerRespawn += OnPlayerRespawn;
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

#pragma warning disable CS0618
        sapi.RegisterCommand(
            "loginprot",
            "Configure Newfie's Login Protection",
            "/loginprot <seconds>|on|off|status",
            HandleCommand,
            "controlserver");
#pragma warning restore CS0618

        sapi.Logger.Notification(
            "[LoginProtection] integrated module started. Enabled={0}, Seconds={1}, Join={2}, Respawn={3}",
            config.Enabled,
            config.ProtectionSeconds,
            config.ProtectOnJoin,
            config.ProtectOnRespawn);
    }

    public override void Dispose()
    {
        if (ReferenceEquals(Current, this)) Current = null;

        if (sapi != null)
        {
            UnhookPlayerReady();
            if (playerJoinHooked)
            {
                try { sapi.Event.PlayerJoin -= OnPlayerJoin; } catch { }
                playerJoinHooked = false;
            }
            if (playerNowPlayingFallbackHooked)
            {
                try { sapi.Event.PlayerNowPlaying -= OnPlayerNowPlayingFallback; } catch { }
                playerNowPlayingFallbackHooked = false;
            }
            if (entityLifecycleHooked)
            {
                try { sapi.Event.OnEntitySpawn -= OnPlayerEntityLifecycle; } catch { }
                try { sapi.Event.OnEntityLoaded -= OnPlayerEntityLifecycle; } catch { }
                entityLifecycleHooked = false;
            }
            sapi.Event.PlayerRespawn -= OnPlayerRespawn;
            sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        }

        if (rebindListenerId >= 0 && sapi != null)
        {
            try { sapi.Event.UnregisterGameTickListener(rebindListenerId); } catch { }
            rebindListenerId = -1;
        }
        if (protectionGuardListenerId >= 0 && sapi != null)
        {
            try { sapi.Event.UnregisterGameTickListener(protectionGuardListenerId); } catch { }
            protectionGuardListenerId = -1;
        }
        pendingRebinds.Clear();
        protectedPlayers.Clear();
        readyForActivation.Clear();
        base.Dispose();
    }

    private void LoadConfig()
    {
        var loaded = ModConfigRecovery.LoadOrNull<LoginProtectionConfig>(
            sapi,
            ConfigFile,
            "LoginProtection");

        if (loaded == null)
        {
            loaded = ModConfigRecovery.LoadOrNull<LoginProtectionConfig>(
                sapi,
                LegacyConfigFile,
                "LoginProtection");
            if (loaded != null)
            {
                sapi.Logger.Notification(
                    "[LoginProtection] Migrating legacy {0} to {1}.",
                    LegacyConfigFile,
                    ConfigFile);
            }
        }

        loaded ??= new LoginProtectionConfig();
        loaded.SchemaVersion = Math.Max(LoginProtectionConfig.CurrentSchemaVersion, loaded.SchemaVersion);
        loaded.Clamp();
        config = loaded;
        SaveConfig();
    }

    public void SaveConfig()
    {
        config.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(sapi, config, ConfigFile);
    }

    private bool TryHookPlayerReady()
    {
        try
        {
            // Avoid a compile-time dependency on PlayerReady. Some 1.22.x API DLLs in the
            // wild do not expose the event even though newer 1.22.7 docs do. Reflection lets
            // this build use the event when present and still compile against those assemblies.
            EventInfo? eventInfo = typeof(IServerEventAPI).GetEvent(
                "PlayerReady",
                BindingFlags.Instance | BindingFlags.Public);

            eventInfo ??= sapi.Event.GetType().GetEvent(
                "PlayerReady",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Type? handlerType = eventInfo?.EventHandlerType;
            MethodInfo? method = GetType().GetMethod(
                nameof(OnPlayerReady),
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo? addMethod = eventInfo?.GetAddMethod(nonPublic: true);

            if (eventInfo == null || handlerType == null || method == null || addMethod == null)
                return false;

            Delegate handler = method.CreateDelegate(handlerType, this);
            addMethod.Invoke(sapi.Event, new object?[] { handler });
            playerReadyEventInfo = eventInfo;
            playerReadyHandler = handler;
            return true;
        }
        catch (Exception ex)
        {
            sapi.Logger.Warning(
                "[LoginProtection] Could not hook optional PlayerReady event ({0}: {1}).",
                ex.GetType().Name,
                ex.Message);
            playerReadyEventInfo = null;
            playerReadyHandler = null;
            return false;
        }
    }

    private void UnhookPlayerReady()
    {
        EventInfo? eventInfo = playerReadyEventInfo;
        Delegate? handler = playerReadyHandler;
        playerReadyEventInfo = null;
        playerReadyHandler = null;
        if (eventInfo == null || handler == null || sapi == null) return;

        try
        {
            eventInfo.GetRemoveMethod(nonPublic: true)?.Invoke(
                sapi.Event,
                new object?[] { handler });
        }
        catch { }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        // A new join always starts a fresh readiness cycle. PlayerJoin happens while the
        // client is still loading, so arm pending state but do not burn protection time yet.
        readyForActivation.Remove(player.PlayerUID);
        EnsureJoinProtection(player, "join");
    }

    private void OnPlayerNowPlayingFallback(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        EnsureJoinProtection(player, "now-playing");

        // Prefer PlayerReady when the API exposes it, because that occurs after character/class
        // selection. Still keep a delayed Playing-state fallback for clients/mod combinations
        // that never raise PlayerReady reliably.
        if (!playerReadyHooked)
        {
            MarkReadyAndActivate(player, "now-playing");
            return;
        }

        string uid = player.PlayerUID;
        sapi.Event.RegisterCallback(_ =>
        {
            var current = OnlinePlayerByUid(uid);
            if (current == null || current.ConnectionState != EnumClientState.Playing) return;
            MarkReadyAndActivate(current, "now-playing-fallback");
        }, PlayerReadyFallbackDelayMs);
    }

    private void OnPlayerReady(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        EnsureJoinProtection(player, "ready");
        MarkReadyAndActivate(player, "ready");
    }

    private void OnPlayerEntityLifecycle(Entity entity)
    {
        if (entity is not EntityPlayer entityPlayer ||
            string.IsNullOrWhiteSpace(entityPlayer.PlayerUID))
            return;

        string uid = entityPlayer.PlayerUID;
        if (!protectedPlayers.ContainsKey(uid) && !pendingRebinds.ContainsKey(uid))
            return;

        var player = OnlinePlayerByUid(uid);
        if (player == null) return;

        QueueRebind(player, recoverSwimming: false);
        TryActivateProtection(player, "entity-ready");
    }

    private void EnsureJoinProtection(IServerPlayer player, string source)
    {
        if (!config.Enabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        if (config.RecoverSwimmingOnLogin)
            QueueRebind(player, recoverSwimming: true);

        if (!config.ProtectOnJoin) return;

        if (protectedPlayers.TryGetValue(player.PlayerUID, out var existing))
        {
            if (existing.Phase == ProtectionPhase.Active &&
                existing.EndsUtcTicks > DateTime.UtcNow.Ticks)
            {
                QueueRebind(player, recoverSwimming: false);
                EnsureProtectionGuardListener();
                return;
            }

            if (existing.Phase is ProtectionPhase.Waiting or ProtectionPhase.Armed)
            {
                QueueRebind(player, recoverSwimming: false);
                EnsureProtectionGuardListener();
                TryActivateProtection(player, source);
                return;
            }
        }

        ArmProtection(player, "join", source, readyImmediately: false);
    }

    private void MarkReadyAndActivate(IServerPlayer player, string source)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;
        readyForActivation.Add(player.PlayerUID);
        TryActivateProtection(player, source);
    }

    private void TryRecoverSwimming(IServerPlayer player)
    {
        var entity = player?.Entity;
        if (entity == null || !entity.Alive) return;

        bool inLiquid = entity.FeetInLiquid || entity.Swimming;
        if (!inLiquid)
        {
            try { inLiquid = entity.IsEyesSubmerged(); }
            catch { }
        }

        if (!inLiquid) return;

        // Swimming is normally determined by the physics system. On reconnect,
        // explicitly restore the movement mode once so gravity/ground movement
        // cannot win during the short initialization window.
        entity.Swimming = true;
        entity.Controls.DetachedMode = true;
        entity.ServerControls.DetachedMode = true;
    }

    private void OnPlayerRespawn(IServerPlayer player)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        if (config.Enabled && config.RecoverSwimmingOnLogin)
            QueueRebind(player, recoverSwimming: true);

        if (config.Enabled && config.ProtectOnRespawn)
        {
            readyForActivation.Add(player.PlayerUID);
            ArmProtection(player, "respawn", "respawn", readyImmediately: true);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        if (player == null) return;
        protectedPlayers.Remove(player.PlayerUID);
        pendingRebinds.Remove(player.PlayerUID);
        readyForActivation.Remove(player.PlayerUID);
        StopRebindListenerIfIdle();
        StopProtectionGuardIfIdle();
    }

    private bool EnsureBehavior(IServerPlayer player)
    {
        if (player?.Entity == null) return false;
        if (player.Entity.GetBehavior<LoginProtectionBehavior>() != null) return true;

        player.Entity.AddBehavior(new LoginProtectionBehavior(player.Entity, this));
        player.Entity.CacheServerBehaviors();
        return player.Entity.GetBehavior<LoginProtectionBehavior>() != null;
    }

    private void QueueRebind(IServerPlayer player, bool recoverSwimming)
    {
        if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (pendingRebinds.TryGetValue(player.PlayerUID, out var existing))
        {
            existing.DeadlineUnixMilliseconds = Math.Max(existing.DeadlineUnixMilliseconds, now + RebindWindowMs);
            existing.RecoverSwimming |= recoverSwimming;
        }
        else
        {
            pendingRebinds[player.PlayerUID] = new RebindState
            {
                DeadlineUnixMilliseconds = now + RebindWindowMs,
                RecoverSwimming = recoverSwimming,
                NextSwimmingCheckUnixMilliseconds = now
            };
        }

        ProcessRebindForPlayer(player.PlayerUID, now);
        if (rebindListenerId < 0 && pendingRebinds.Count > 0)
        {
            rebindListenerId = sapi.Event.RegisterGameTickListener(
                _ => ProcessPendingRebinds(),
                ex => sapi.Logger.Warning(
                    "[LoginProtection] entity rebind pass failed: {0}: {1}",
                    ex.GetType().Name,
                    ex.Message),
                RebindIntervalMs);
        }
    }

    private void ProcessPendingRebinds()
    {
        long timingStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (pendingRebinds.Count == 0)
        {
            StopRebindListenerIfIdle();
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (string uid in pendingRebinds.Keys.ToArray())
            ProcessRebindForPlayer(uid, now);

        StopRebindListenerIfIdle();
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - timingStart) /
                           (double)System.Diagnostics.Stopwatch.Frequency * 1000d;
        SuiteTimingRegistry.Record("Login Protection rebind", elapsedMs);
    }

    private void ProcessRebindForPlayer(string uid, long now)
    {
        if (!pendingRebinds.TryGetValue(uid, out var rebind)) return;
        var player = OnlinePlayerByUid(uid);

        if (player != null && player.Entity != null)
        {
            if (config.Enabled && EnsureBehavior(player))
            {
                rebind.SawBehavior = true;
                if (protectedPlayers.TryGetValue(uid, out var protection) &&
                    protection.Phase == ProtectionPhase.Waiting)
                {
                    protection.Phase = ProtectionPhase.Armed;
                }

                TryActivateProtection(player, "rebind");
            }

            if (rebind.RecoverSwimming &&
                rebind.SwimmingAttempts < 2 &&
                now >= rebind.NextSwimmingCheckUnixMilliseconds)
            {
                TryRecoverSwimming(player);
                rebind.SwimmingAttempts++;
                rebind.NextSwimmingCheckUnixMilliseconds = now + RebindIntervalMs;
            }
        }

        if (now < rebind.DeadlineUnixMilliseconds) return;

        if (!rebind.SawBehavior &&
            protectedPlayers.TryGetValue(uid, out var active) &&
            (active.EndsUtcTicks == 0 || active.EndsUtcTicks > DateTime.UtcNow.Ticks) &&
            !rebind.WarningLogged)
        {
            rebind.WarningLogged = true;
            sapi.Logger.Warning(
                "[LoginProtection] protection remained armed for {0} but no current player entity received the damage behavior during the initialization window.",
                active.PlayerName);
        }

        pendingRebinds.Remove(uid);
    }

    private void StopRebindListenerIfIdle()
    {
        if (pendingRebinds.Count != 0 || rebindListenerId < 0) return;
        try { sapi.Event.UnregisterGameTickListener(rebindListenerId); } catch { }
        rebindListenerId = -1;
    }

    private void ArmProtection(
        IServerPlayer player,
        string reason,
        string source,
        bool readyImmediately)
    {
        if (!config.Enabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID)) return;

        long now = DateTime.UtcNow.Ticks;
        if (!string.Equals(reason, "respawn", StringComparison.OrdinalIgnoreCase) &&
            protectedPlayers.TryGetValue(player.PlayerUID, out var existing) &&
            existing.Phase == ProtectionPhase.Active &&
            existing.EndsUtcTicks > now)
        {
            QueueRebind(player, recoverSwimming: false);
            return;
        }

        long generation = ++protectionGeneration;
        protectedPlayers[player.PlayerUID] =
            new ProtectionState(generation, player.PlayerName, reason)
            {
                Phase = player.Entity == null ? ProtectionPhase.Waiting : ProtectionPhase.Armed
            };

        if (readyImmediately)
            readyForActivation.Add(player.PlayerUID);

        QueueRebind(player, recoverSwimming: false);
        EnsureProtectionGuardListener();
        TryActivateProtection(player, source);

        sapi.Logger.Debug(
            "[LoginProtection] Armed {0} protection for {1} from {2}; countdown waits for a ready, bound player entity.",
            reason,
            player.PlayerName,
            source);
    }

    private void TryActivateProtection(IServerPlayer player, string source)
    {
        if (!config.Enabled ||
            player == null ||
            string.IsNullOrWhiteSpace(player.PlayerUID) ||
            !readyForActivation.Contains(player.PlayerUID) ||
            player.ConnectionState != EnumClientState.Playing ||
            player.Entity == null ||
            !player.Entity.Alive)
        {
            return;
        }

        if (!protectedPlayers.TryGetValue(player.PlayerUID, out var state))
            return;

        if (state.Phase == ProtectionPhase.Active &&
            state.EndsUtcTicks > DateTime.UtcNow.Ticks)
        {
            EnsureBehavior(player);
            return;
        }

        if (!EnsureBehavior(player))
            return;

        int seconds = Math.Clamp(config.ProtectionSeconds, 1, 3600);
        state.Phase = ProtectionPhase.Active;
        state.ActivatedUtcTicks = DateTime.UtcNow.Ticks;
        state.EndsUtcTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks;

        if (config.ExtinguishFire)
            player.Entity.IsOnFire = false;

        if (!state.StartAnnounced && config.AnnounceOnJoin)
        {
            state.StartAnnounced = true;
            SendPlayerMessage(
                player,
                state.Reason == "respawn"
                    ? $"Respawn protection active for {seconds}s."
                    : $"Login protection active for {seconds}s.");
        }

        long generation = state.Generation;
        sapi.Event.RegisterCallback(
            _ => ExpireProtection(player.PlayerUID, generation),
            seconds * 1000);

        sapi.Logger.Debug(
            "[LoginProtection] Activated {0} protection for {1} from {2}; full {3}s starts now.",
            state.Reason,
            player.PlayerName,
            source,
            seconds);
    }

    private void ExpireProtection(string playerUid, long generation)
    {
        if (!protectedPlayers.TryGetValue(playerUid, out var state)) return;
        if (state.Generation != generation) return;

        state.Phase = ProtectionPhase.Expired;
        protectedPlayers.Remove(playerUid);
        StopProtectionGuardIfIdle();

        if (!config.Enabled || !config.AnnounceOnEnd) return;

        var player = OnlinePlayerByUid(playerUid);
        if (player != null)
            SendPlayerMessage(player, "Login protection disabled — you can now take damage.");
    }

    private void CancelProtection(
        string playerUid,
        string message,
        bool announce)
    {
        if (!protectedPlayers.Remove(playerUid)) return;
        StopProtectionGuardIfIdle();

        if (!announce) return;

        var player = OnlinePlayerByUid(playerUid);
        if (player != null)
            SendPlayerMessage(player, message);
    }

    public bool IsProtected(string playerUid)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(playerUid)) return false;

        if (!protectedPlayers.TryGetValue(playerUid, out var state))
            return false;

        if (state.Phase != ProtectionPhase.Active || state.EndsUtcTicks <= 0)
            return false;

        if (DateTime.UtcNow.Ticks < state.EndsUtcTicks)
            return true;

        state.Phase = ProtectionPhase.Expired;
        protectedPlayers.Remove(playerUid);
        StopProtectionGuardIfIdle();

        if (config.AnnounceOnEnd)
        {
            var player = OnlinePlayerByUid(playerUid);
            if (player != null)
                SendPlayerMessage(player, "Login protection disabled — you can now take damage.");
        }

        return false;
    }

    /// <summary>
    /// Called from the behavior attached to every online player entity.
    /// This covers both protection of the target and anti-abuse PvP blocking.
    /// </summary>
    public bool ShouldBlockIncomingDamage(EntityPlayer target, DamageSource source)
    {
        if (!config.Enabled || target == null) return false;

        string targetUid = target.PlayerUID ?? string.Empty;
        bool targetProtected = IsProtected(targetUid);

        // A protected attacker may not damage another player if PvP blocking is on.
        // GetCauseEntity handles both direct melee and projectiles.
        Entity? cause = null;
        try { cause = source?.GetCauseEntity(); } catch { }

        if (cause is EntityPlayer attacker &&
            !string.Equals(attacker.PlayerUID, targetUid, StringComparison.Ordinal) &&
            IsProtected(attacker.PlayerUID))
        {
            if (config.BlockPvP)
            {
                if (config.CancelOnAttack)
                {
                    CancelProtection(
                        attacker.PlayerUID,
                        "Login protection cancelled because you attacked another player.",
                        announce: true);
                }

                return true;
            }
        }

        if (!targetProtected) return false;

        if (source != null &&
            source.Source == EnumDamageSource.Fall &&
            !config.ProtectFallDamage)
        {
            return false;
        }

        if (config.ExtinguishFire && target.IsOnFire)
            target.IsOnFire = false;

        return true;
    }

    public void OnProtectedPlayerAttacked(EntityPlayer attacker)
    {
        if (!config.Enabled || !config.CancelOnAttack || attacker == null) return;
        if (!IsProtected(attacker.PlayerUID)) return;

        CancelProtection(
            attacker.PlayerUID,
            "Login protection cancelled because you attacked.",
            announce: true);
    }

    public List<string> GetGuiStatusLines()
    {
        PruneExpiredProtection();

        var lines = new List<string>
        {
            $"Login Protection: {(config.Enabled ? "ENABLED" : "DISABLED")}",
            $"Protection duration: {config.ProtectionSeconds}s",
            $"Protect on join: {(config.ProtectOnJoin ? "ON" : "OFF")}",
            $"Protect on respawn: {(config.ProtectOnRespawn ? "ON" : "OFF")}",
            $"Block PvP while protected: {(config.BlockPvP ? "ON" : "OFF")}",
            $"Cancel on attack: {(config.CancelOnAttack ? "ON" : "OFF")}",
            $"Protect fall damage: {(config.ProtectFallDamage ? "ON" : "OFF")}",
            $"Extinguish fire: {(config.ExtinguishFire ? "ON" : "OFF")}",
            $"Water-login swim recovery: {(config.RecoverSwimmingOnLogin ? "ON" : "OFF")}",
            $"Currently protected: {ActiveProtectedCount}",
            $"Waiting for ready entity: {PendingProtectedCount}",
            "",
            "Protected players:"
        };

        var now = DateTime.UtcNow.Ticks;
        foreach (var pair in protectedPlayers
                     .Where(x => x.Value.Phase == ProtectionPhase.Active && x.Value.EndsUtcTicks > now)
                     .OrderBy(x => x.Value.EndsUtcTicks)
                     .Take(40))
        {
            int remaining = Math.Max(
                0,
                (int)Math.Ceiling(
                    TimeSpan.FromTicks(pair.Value.EndsUtcTicks - now).TotalSeconds));
            lines.Add($"  {pair.Value.PlayerName}: {remaining}s remaining");
        }

        foreach (var pair in protectedPlayers
                     .Where(x => x.Value.Phase is ProtectionPhase.Waiting or ProtectionPhase.Armed)
                     .Take(40))
        {
            lines.Add($"  {pair.Value.PlayerName}: waiting for player-ready/entity bind");
        }

        if (protectedPlayers.Count == 0)
            lines.Add("  (none)");

        return lines;
    }

    public List<string> GetGuiSettingsLines() =>
        new()
        {
            $"Login Protection: {(config.Enabled ? "ENABLED" : "DISABLED")}",
            $"Protection duration: {config.ProtectionSeconds}s",
            $"Currently protected: {ActiveProtectedCount}",
            "",
            "Changes are saved immediately to NewfiesLoginProtection.json."
        };

    public string SetBooleanSettingFromGui(string? key, bool value)
    {
        string normalized = (key ?? "").Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "enabled":
            {
                bool wasEnabled = config.Enabled;
                config.Enabled = value;

                // Existing online players do not need a behavior attached merely because the
                // feature was enabled. The next protected join/respawn attaches it on demand.
                if (!value && wasEnabled)
                    DisableAllProtection();

                break;
            }

            case "protectonjoin":
                config.ProtectOnJoin = value;
                break;

            case "protectonrespawn":
                config.ProtectOnRespawn = value;
                break;

            case "announceonjoin":
                config.AnnounceOnJoin = value;
                break;

            case "announceonend":
                config.AnnounceOnEnd = value;
                break;

            case "blockpvp":
                config.BlockPvP = value;
                break;

            case "cancelonattack":
                config.CancelOnAttack = value;
                break;

            case "protectfalldamage":
                config.ProtectFallDamage = value;
                break;

            case "extinguishfire":
                config.ExtinguishFire = value;
                break;

            case "recoverswimmingonlogin":
                config.RecoverSwimmingOnLogin = value;
                break;

            default:
                return $"Unknown Login Protection setting '{key}'.";
        }

        SaveConfig();
        return "Login Protection setting saved.";
    }

    public string SetIntegerSettingFromGui(string? key, int value)
    {
        string normalized = (key ?? "").Trim().ToLowerInvariant();

        if (normalized != "protectionseconds")
            return $"Unknown Login Protection setting '{key}'.";

        config.ProtectionSeconds = Math.Clamp(value, 1, 3600);
        SaveConfig();
        return $"Protection duration set to {config.ProtectionSeconds}s.";
    }

    private void DisableAllProtection()
    {
        var online = sapi.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .Where(p => protectedPlayers.ContainsKey(p.PlayerUID))
            .ToList();

        protectedPlayers.Clear();
        readyForActivation.Clear();
        StopProtectionGuardIfIdle();

        foreach (var player in online)
            SendPlayerMessage(player, "Login protection disabled by an administrator.");
    }

    private void PruneExpiredProtection()
    {
        if (protectedPlayers.Count == 0) return;

        long now = DateTime.UtcNow.Ticks;
        foreach (var uid in protectedPlayers
                     .Where(x =>
                         x.Value.Phase == ProtectionPhase.Active &&
                         x.Value.EndsUtcTicks > 0 &&
                         x.Value.EndsUtcTicks <= now)
                     .Select(x => x.Key)
                     .ToList())
        {
            protectedPlayers.Remove(uid);
        }
        StopProtectionGuardIfIdle();
    }

    private void EnsureProtectionGuardListener()
    {
        if (protectionGuardListenerId >= 0 || protectedPlayers.Count == 0) return;
        protectionGuardListenerId = sapi.Event.RegisterGameTickListener(_ => ProtectionGuardPass(), ProtectionGuardIntervalMs);
    }

    private void ProtectionGuardPass()
    {
        PruneExpiredProtection();
        if (protectedPlayers.Count == 0) { StopProtectionGuardIfIdle(); return; }
        foreach (string uid in protectedPlayers.Keys.ToArray())
        {
            var player = OnlinePlayerByUid(uid);
            if (player?.Entity == null) continue;

            if (EnsureBehavior(player) &&
                protectedPlayers.TryGetValue(uid, out var state) &&
                state.Phase == ProtectionPhase.Waiting)
            {
                state.Phase = ProtectionPhase.Armed;
            }

            TryActivateProtection(player, "guard");
        }
    }

    private void StopProtectionGuardIfIdle()
    {
        if (protectedPlayers.Count != 0 || protectionGuardListenerId < 0 || sapi == null) return;
        try { sapi.Event.UnregisterGameTickListener(protectionGuardListenerId); } catch { }
        protectionGuardListenerId = -1;
    }

    private IServerPlayer? OnlinePlayerByUid(string playerUid) =>
        sapi.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .FirstOrDefault(
                p => string.Equals(p.PlayerUID, playerUid, StringComparison.Ordinal));

    private void SendPlayerMessage(IServerPlayer player, string text) =>
        sapi.SendMessage(
            player,
            GlobalConstants.GeneralChatGroup,
            text,
            EnumChatType.Notification);

    private void HandleCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        string word = (args.PopWord() ?? "").Trim();

        if (word.Length == 0 || word.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            sapi.SendMessage(
                player,
                groupId,
                $"Login Protection: {(config.Enabled ? "ON" : "OFF")}; " +
                $"{config.ProtectionSeconds}s; active={ActiveProtectedCount}; " +
                $"join={config.ProtectOnJoin}; respawn={config.ProtectOnRespawn}",
                EnumChatType.CommandSuccess);
            return;
        }

        if (word.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            SetBooleanSettingFromGui("enabled", true);
            sapi.SendMessage(player, groupId, "Login Protection enabled.", EnumChatType.CommandSuccess);
            return;
        }

        if (word.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            SetBooleanSettingFromGui("enabled", false);
            sapi.SendMessage(player, groupId, "Login Protection disabled.", EnumChatType.CommandSuccess);
            return;
        }

        if (!int.TryParse(word, out int seconds))
        {
            sapi.SendMessage(
                player,
                groupId,
                "Usage: /loginprot <seconds>|on|off|status",
                EnumChatType.CommandError);
            return;
        }

        config.ProtectionSeconds = Math.Clamp(seconds, 1, 3600);
        SaveConfig();

        sapi.SendMessage(
            player,
            groupId,
            $"Login protection duration set to {config.ProtectionSeconds}s.",
            EnumChatType.CommandSuccess);
    }

    private enum ProtectionPhase
    {
        Waiting,
        Armed,
        Active,
        Expired
    }

    private sealed class ProtectionState
    {
        public long Generation { get; }
        public long ActivatedUtcTicks { get; set; }
        public long EndsUtcTicks { get; set; }
        public string PlayerName { get; }
        public string Reason { get; }
        public bool StartAnnounced { get; set; }
        public ProtectionPhase Phase { get; set; }

        public ProtectionState(long generation, string playerName, string reason)
        {
            Generation = generation;
            PlayerName = playerName;
            Reason = reason;
            Phase = ProtectionPhase.Waiting;
        }
    }

    private sealed class RebindState
    {
        public long DeadlineUnixMilliseconds { get; set; }
        public bool RecoverSwimming { get; set; }
        public int SwimmingAttempts { get; set; }
        public long NextSwimmingCheckUnixMilliseconds { get; set; }
        public bool SawBehavior { get; set; }
        public bool WarningLogged { get; set; }
    }
}

/// <summary>
/// Attached to each server-side player entity. It participates in the normal
/// Vintage Story entity damage pipeline and asks the UID-authoritative module
/// whether the incoming damage should be suppressed.
/// </summary>
public sealed class LoginProtectionBehavior : EntityBehavior
{
    private readonly LoginProtectionSystem system;

    public LoginProtectionBehavior(
        Entity entity,
        LoginProtectionSystem system) : base(entity)
    {
        this.system = system;
    }

    public override string PropertyName() => "newfiesloginprotection";

    public override void OnEntityReceiveDamage(
        DamageSource damageSource,
        ref float damage)
    {
        if (damage <= 0) return;
        if (entity is not EntityPlayer player) return;

        if (system.ShouldBlockIncomingDamage(player, damageSource))
            damage = 0;
    }

    public override void DidAttack(
        DamageSource source,
        EntityAgent targetEntity,
        ref EnumHandling handled)
    {
        if (entity is EntityPlayer player)
            system.OnProtectedPlayerAttacked(player);
    }
}
