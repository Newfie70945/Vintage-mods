using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using BlockLogger.Config;
using BlockLogger.Services;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Afk;

public class NewfiesAfkModSystem : ModSystem, IRenderer
{
    public const string ChannelName = "newfies-suite-afk-v1";
    public const string ConfigName = "NewfiesAfk.json";
    public const string AfkPrivilegeCode = "afk";
    public const int ProtocolVersion = 1;
    private const string HarmonyId = "newfiesserveressentialssuite.afk";
    private const int HelloRetryMs = 1000;
    private const int MaxHelloAttempts = 30;

    private ICoreServerAPI? sapi;
    private ICoreClientAPI? capi;
    private IServerNetworkChannel? serverChannel;
    private IClientNetworkChannel? clientChannel;
    private AfkConfig config = new();
    private Harmony? harmony;
    private bool xSkillsCompatibilityPatched;
    private bool hungerCompatibilityPatched;
    private bool hydrateOrDiedrateCompatibilityPatched;

    internal static NewfiesAfkModSystem? Instance { get; private set; }
    public static NewfiesAfkModSystem? Current => Instance;
    public bool RootAfkCommandRegistered { get; private set; }

    private readonly Dictionary<string, AfkPlayerState> afkPlayers = new();
    private readonly HashSet<string> cameraCapablePlayers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastDamageUnixMsByPlayerUid = new();
    private readonly Dictionary<string, long> lastCorrectionUnixMsByPlayerUid = new();

    private bool clientAfkCameraEnabled;
    private float previousCameraPitch;
    private float previousCameraYaw;
    private double orbitAngleRadians;
    private AfkCameraPacket currentCameraPacket = new();
    private object? previousCameraMode;
    private bool previousCameraModeWasCaptured;
    private long lastThirdPersonForceMs;
    private long clientCameraTickId;
    private float lastAppliedOrbitYaw;
    private double orbitBaseYawRadians;
    private long clientCameraStartMs;
    private bool renderFrameCameraRegistered;
    private long serverLockTickId = -1;
    private long clientHelloListenerId = -1;
    private bool clientHelloSent;
    private int clientHelloAttempts;
    private bool clientLevelFinalizeHooked;
    private bool serverEventsHooked;
    private bool serverStarted;
    private bool clientStarted;

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        // The standalone NewfiesAfk mod is now integrated into Server Essentials Suite.
        // If an old copy is still installed, fail this subsystem open instead of fighting
        // over /afk, network channels, or Harmony patches.
        if (api.ModLoader.Mods.Any(mod =>
                mod?.Info != null &&
                string.Equals(mod.Info.ModID, "newfiesafk", StringComparison.OrdinalIgnoreCase)))
        {
            api.Logger.Error(
                "[ServerEssentials/AFK] Standalone NewfiesAfk is also installed. Remove the standalone mod; the integrated AFK subsystem has been disabled for this run to avoid duplicate commands/patches.");
            return;
        }

        SuiteConfigStorage.InitializeAndRelocateLegacyFiles(api);
        var suiteFeatures = SuiteFeatureConfig.Load(api);
        if (!suiteFeatures.Afk)
        {
            api.Logger.Notification(
                "[ServerEssentials/AFK] Entire subsystem disabled by {0}; no AFK command, lock tick, survival patches, or camera channel registered.",
                SuiteFeatureConfig.ConfigFile);
            return;
        }

        Instance = this;
        serverStarted = true;
        config = LoadOrCreateConfig(api);

        api.Permissions.RegisterPrivilege(AfkPrivilegeCode, "Ability to use the /afk command.");

        harmony = new Harmony(HarmonyId);
        TryInstallDamageTrackingPatch();
        TryInstallXSkillsCompatibility();
        TryInstallHungerCompatibility();
        TryInstallHydrateOrDiedrateCompatibility();

        serverChannel = api.Network.GetChannel(ChannelName) as IServerNetworkChannel
            ?? api.Network.RegisterChannel(ChannelName);
        serverChannel
            .RegisterMessageType(typeof(AfkCameraPacket))
            .RegisterMessageType(typeof(AfkClientHelloPacket))
            .SetMessageHandler<AfkClientHelloPacket>(OnAfkClientHello);

        if (api.ChatCommands.Get("afk") == null)
        {
            api.ChatCommands
                .Create("afk")
                .WithDescription("Toggle locked spectator AFK mode.")
                .RequiresPrivilege(AfkPrivilegeCode)
                .RequiresPlayer()
                .HandleWith(OnAfkCommand);
            RootAfkCommandRegistered = true;
        }
        else
        {
            api.Logger.Warning(
                "[ServerEssentials/AFK] Root /afk is already owned by another provider. Use /nse afk as the Suite fallback.");
        }

        if (api.ChatCommands.Get("afkstatus") == null)
        {
            api.ChatCommands
                .Create("afkstatus")
                .WithDescription("Show Server Essentials AFK debug status.")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(OnAfkStatusCommand);
        }

        if (config.DebugLogging)
        {
            api.Logger.Notification("[ServerEssentials/AFK] Server loaded. CombatCooldownSeconds={0}, UseSpectatorMode={1}, EnableClientCamera={2}, LockTolerance={3}, DriftCorrectionIntervalMs={4}", config.CombatCooldownSeconds, config.UseSpectatorMode, config.EnableClientCamera, config.LockDistanceTolerance, config.DriftCorrectionIntervalMs);
        }

        serverLockTickId = api.Event.RegisterGameTickListener(OnServerLockTick, Math.Max(20, config.LockIntervalMs));
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.PlayerDeath += OnPlayerDeath;
        serverEventsHooked = true;
    }

    public override void Dispose()
    {
        if (serverEventsHooked && sapi != null)
        {
            try { sapi.Event.PlayerDisconnect -= OnPlayerDisconnect; } catch { }
            try { sapi.Event.PlayerDeath -= OnPlayerDeath; } catch { }
            serverEventsHooked = false;
        }

        if (serverLockTickId >= 0 && sapi != null)
        {
            try { sapi.Event.UnregisterGameTickListener(serverLockTickId); } catch { }
            serverLockTickId = -1;
        }

        if (clientLevelFinalizeHooked && capi != null)
        {
            try { capi.Event.LevelFinalize -= OnClientLevelFinalize; } catch { }
            clientLevelFinalizeHooked = false;
        }

        StopClientHelloLoop();
        if (clientStarted)
        {
            try { StopClientAfkCamera(restoreViewAngles: true, showMessage: false); } catch { }
            clientStarted = false;
        }

        try { harmony?.UnpatchAll(HarmonyId); } catch { }
        harmony = null;

        cameraCapablePlayers.Clear();
        afkPlayers.Clear();
        lastDamageUnixMsByPlayerUid.Clear();
        lastCorrectionUnixMsByPlayerUid.Clear();

        if (Instance == this)
        {
            Instance = null;
        }

        serverStarted = false;
        base.Dispose();
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        if (api.ModLoader.Mods.Any(mod =>
                mod?.Info != null &&
                string.Equals(mod.Info.ModID, "newfiesafk", StringComparison.OrdinalIgnoreCase)))
        {
            api.Logger.Warning(
                "[ServerEssentials/AFK] Standalone NewfiesAfk is installed on this client. Integrated AFK camera support is disabled to avoid duplicate camera control.");
            return;
        }

        clientStarted = true;
        config = LoadOrCreateConfig(api);

        api.Logger.Notification("[ServerEssentials/AFK] Client camera support loaded. UseCameraOffset={0}, UseVanillaThirdPersonZoom={1}, CameraDistance={2}", config.UseCameraOffset, config.UseVanillaThirdPersonZoom, config.CameraDistance);

        clientChannel = api.Network.GetChannel(ChannelName) as IClientNetworkChannel
            ?? api.Network.RegisterChannel(ChannelName);
        clientChannel
            .RegisterMessageType(typeof(AfkCameraPacket))
            .RegisterMessageType(typeof(AfkClientHelloPacket))
            .SetMessageHandler<AfkCameraPacket>(OnAfkCameraPacket);

        api.Event.LevelFinalize += OnClientLevelFinalize;
        clientLevelFinalizeHooked = true;
        StartClientHelloLoop();
        if (api.PlayerReadyFired) TrySendClientHello();

        // Camera tick is registered only while AFK camera is active. This avoids idle client overhead and reduces camera fighting.
    }

    private AfkConfig LoadOrCreateConfig(ICoreAPI api)
    {
        AfkConfig? loaded = null;

        try
        {
            if (api is ICoreServerAPI serverApi)
            {
                loaded = ModConfigRecovery.LoadOrNull<AfkConfig>(
                    serverApi,
                    ConfigName,
                    "ServerEssentials/AFK");
            }
            else
            {
                // Client camera settings are local to that client. Prefer the Suite folder,
                // but import the old standalone root config once when present.
                try
                {
                    Directory.CreateDirectory(Path.Combine(api.DataBasePath, "ModConfig", SuiteConfigStorage.FolderName));
                }
                catch { }

                loaded = api.LoadModConfig<AfkConfig>(SuiteConfigStorage.RelativePath(ConfigName));
                if (loaded == null)
                {
                    loaded = api.LoadModConfig<AfkConfig>(ConfigName);
                    if (loaded != null)
                    {
                        api.Logger.Notification(
                            "[ServerEssentials/AFK] Importing legacy client AFK config into ModConfig/{0}/.",
                            SuiteConfigStorage.FolderName);
                    }
                }
            }
        }
        catch (ConfigMigrationException)
        {
            // Migration failures are not corrupt configs. Preserve the source/backup and let
            // startup fail this AFK subsystem rather than overwriting it with defaults.
            throw;
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/AFK] Could not load config, using defaults. Error: {0}", ex.Message);
        }

        loaded ??= new AfkConfig();
        loaded.Clamp();

        if (api is ICoreServerAPI sapiConfig)
            SuiteConfigStorage.Store(sapiConfig, loaded, ConfigName);
        else
            api.StoreModConfig(loaded, SuiteConfigStorage.RelativePath(ConfigName));

        return loaded;
    }

    private void OnAfkClientHello(IServerPlayer player, AfkClientHelloPacket packet)
    {
        if (player == null || packet == null || packet.ProtocolVersion != ProtocolVersion) return;

        cameraCapablePlayers.Add(player.PlayerUID);
        if (config.DebugLogging)
        {
            sapi?.Logger.Debug(
                "[ServerEssentials/AFK] Client camera handshake accepted for {0}; clientVersion={1}.",
                player.PlayerName,
                packet.ClientVersion ?? "");
        }

        // If the hello arrives after /afk was already enabled, synchronize camera state now.
        if (afkPlayers.ContainsKey(player.PlayerUID))
            SendCameraPacketIfSupported(player, enabled: true);
    }

    private void OnClientLevelFinalize()
    {
        clientHelloSent = false;
        clientHelloAttempts = 0;
        StartClientHelloLoop();
        TrySendClientHello();
    }

    private void StartClientHelloLoop()
    {
        if (capi == null || clientHelloSent || clientHelloListenerId >= 0) return;
        if (TrySendClientHello()) return;

        clientHelloListenerId = capi.Event.RegisterGameTickListener(_ =>
        {
            if (clientHelloSent || clientHelloAttempts >= MaxHelloAttempts)
            {
                StopClientHelloLoop();
                return;
            }

            clientHelloAttempts++;
            TrySendClientHello();
        }, HelloRetryMs);
    }

    private void StopClientHelloLoop()
    {
        if (capi == null || clientHelloListenerId < 0) return;
        try { capi.Event.UnregisterGameTickListener(clientHelloListenerId); } catch { }
        clientHelloListenerId = -1;
    }

    private bool TrySendClientHello()
    {
        if (capi == null || clientChannel == null || clientHelloSent) return clientHelloSent;
        if (capi.Network.GetChannelState(ChannelName) != EnumChannelState.Connected) return false;

        try
        {
            clientChannel.SendPacket(new AfkClientHelloPacket
            {
                ProtocolVersion = ProtocolVersion,
                ClientVersion = SuiteRelease.Version
            });
            clientHelloSent = true;
            StopClientHelloLoop();
            return true;
        }
        catch (Exception ex)
        {
            if (config.DebugLogging)
                capi.Logger.Debug("[ServerEssentials/AFK] Camera hello not ready: {0}: {1}", ex.GetType().Name, ex.Message);
            return false;
        }
    }

    private void SendCameraPacketIfSupported(IServerPlayer player, bool enabled)
    {
        if (serverChannel == null || player == null) return;
        if (!cameraCapablePlayers.Contains(player.PlayerUID))
        {
            if (config.DebugLogging)
            {
                sapi?.Logger.Debug(
                    "[ServerEssentials/AFK] {0} has no integrated Suite AFK client handshake; server AFK state will run without the orbit camera.",
                    player.PlayerName);
            }
            return;
        }

        try
        {
            serverChannel.SendPacket(BuildCameraPacket(enabled), player);
        }
        catch (Exception ex)
        {
            cameraCapablePlayers.Remove(player.PlayerUID);
            sapi?.Logger.Debug(
                "[ServerEssentials/AFK] Camera packet failed for {0}; client capability was cleared: {1}: {2}",
                player.PlayerName,
                ex.GetType().Name,
                ex.Message);
        }
    }

    public bool IsAfk(string? playerUid) =>
        !string.IsNullOrWhiteSpace(playerUid) && afkPlayers.ContainsKey(playerUid);

    public (bool Success, string Message) ToggleForPlayer(IServerPlayer player)
    {
        if (sapi == null || serverChannel == null || !serverStarted)
            return (false, "AFK system is not ready.");

        if (player?.Entity == null)
            return (false, "Only an online player can use /afk.");

        if (afkPlayers.ContainsKey(player.PlayerUID))
        {
            DisableAfk(player, false);
            return (true, "AFK mode disabled.");
        }

        if (IsCombatCooldownActive(player, out int secondsRemaining))
        {
            string message = FormatCombatBlockMessage(secondsRemaining);
            player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
            return (false, message);
        }

        if (config.RequireGroundedToEnable && !IsStandingOnGroundForAfk(player.Entity, out string groundError))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, groundError, EnumChatType.Notification);
            return (false, groundError);
        }

        if (!EnableAfk(player, out string enableError))
            return (false, enableError);

        return (true, "AFK mode enabled.");
    }

    private TextCommandResult OnAfkCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Only an online player can use /afk.");

        var result = ToggleForPlayer(player);
        return result.Success
            ? TextCommandResult.Success(result.Message)
            : TextCommandResult.Error(result.Message);
    }


    private TextCommandResult OnAfkStatusCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
        {
            return TextCommandResult.Error("Only an online player can use this command.");
        }

        bool isAfk = afkPlayers.ContainsKey(player.PlayerUID);
        bool combat = IsCombatCooldownActive(player, out int secondsRemaining);
        return TextCommandResult.Success($"ServerEssentials AFK: loaded=true, youAfk={isAfk}, afkPlayers={afkPlayers.Count}, combatCooldownActive={combat}, secondsRemaining={secondsRemaining}, xskillsXpBlock={config.DisableXSkillsSurvivalXpWhileAfk}, xskillsPatched={xSkillsCompatibilityPatched}, stopMobSpawns={config.StopMobSpawnsAroundAfkPlayers}, pauseHunger={config.PauseHungerWhileAfk}, hungerPatched={hungerCompatibilityPatched}, pauseHoDThirst={config.PauseHydrateOrDiedrateThirstWhileAfk}, hodPatched={hydrateOrDiedrateCompatibilityPatched}, autoKick={config.AutoKickAfkEnabled}, autoKickMinutes={config.AutoKickAfterMinutes:0.##}, clientCamera={config.EnableClientCamera}, serverEntitySpin={config.EnableServerEntitySpin}, spinDegPerSec={config.CameraOrbitSpeedDegreesPerSecond:0.##}, debug={config.DebugLogging}");
    }

    private bool EnableAfk(IServerPlayer player, out string error)
    {
        error = string.Empty;

        if (sapi == null || serverChannel == null || player.Entity == null)
        {
            error = "AFK system is not ready.";
            return false;
        }

        if (!TryReadEntityPosition(player.Entity, out AfkPlayerState state))
        {
            error = "Could not read your player position. Check server-main.log for ServerEssentials/AFK details.";
            sapi.Logger.Error("[ServerEssentials/AFK] Could not read position for {0}; AFK was not enabled.", player.PlayerName);
            return false;
        }

        state.OriginalGameMode = player.WorldData.CurrentGameMode;
        state.EnteredAfkUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        state.AutoKickWarningSent = false;

        afkPlayers[player.PlayerUID] = state;
        lastCorrectionUnixMsByPlayerUid[player.PlayerUID] = 0;

        if (config.StopMobSpawnsAroundAfkPlayers)
        {
            player.Entity.OnCanSpawnNearby -= DenyCreatureSpawnNearAfkPlayer;
            player.Entity.OnCanSpawnNearby += DenyCreatureSpawnNearAfkPlayer;
        }

        if (config.DebugLogging)
        {
            sapi.Logger.Notification("[ServerEssentials/AFK] Enabling AFK for {0} at {1:0.00},{2:0.00},{3:0.00}", player.PlayerName, state.X, state.Y, state.Z);
        }

        if (config.UseSpectatorMode)
        {
            player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
            player.BroadcastPlayerData();
        }

        ZeroControlsAndMotion(player.Entity);

        SendCameraPacketIfSupported(player, enabled: true);

        player.SendMessage(GlobalConstants.GeneralChatGroup, "You are now AFK. Use /afk again to return.", EnumChatType.Notification);

        if (config.BroadcastMessages)
        {
            sapi.BroadcastMessageToAllGroups($"{player.PlayerName} is now AFK.", EnumChatType.Notification);
        }

        return true;
    }

    private void DisableAfk(IServerPlayer player, bool restorePosition)
    {
        if (sapi == null || serverChannel == null) return;
        if (!afkPlayers.TryGetValue(player.PlayerUID, out AfkPlayerState? state)) return;

        if (player.Entity != null)
        {
            player.Entity.OnCanSpawnNearby -= DenyCreatureSpawnNearAfkPlayer;
        }

        afkPlayers.Remove(player.PlayerUID);
        lastCorrectionUnixMsByPlayerUid.Remove(player.PlayerUID);

        if (config.DebugLogging)
        {
            sapi.Logger.Notification("[ServerEssentials/AFK] Disabling AFK for {0}. restorePosition={1}", player.PlayerName, restorePosition);
        }

        if (restorePosition && player.Entity != null)
        {
            TeleportEntityToLockedPos(player.Entity, state);
            ZeroControlsAndMotion(player.Entity);
        }

        player.WorldData.CurrentGameMode = GetReturnGameMode(state);
        player.BroadcastPlayerData();

        SendCameraPacketIfSupported(player, enabled: false);

        player.SendMessage(GlobalConstants.GeneralChatGroup, "You are no longer AFK.", EnumChatType.Notification);

        if (config.BroadcastMessages)
        {
            sapi.BroadcastMessageToAllGroups($"{player.PlayerName} is no longer AFK.", EnumChatType.Notification);
        }
    }

    private EnumGameMode GetReturnGameMode(AfkPlayerState state)
    {
        if (config.RestoreOriginalGameMode)
        {
            return state.OriginalGameMode;
        }

        if (!string.IsNullOrWhiteSpace(config.ReturnGameMode) && Enum.TryParse(config.ReturnGameMode, true, out EnumGameMode parsed))
        {
            return parsed;
        }

        return EnumGameMode.Survival;
    }

    private bool IsStandingOnGroundForAfk(EntityPlayer entity, out string error)
    {
        error = string.IsNullOrWhiteSpace(config.NotGroundedBlockMessage)
            ? "You must be standing on solid ground to use /afk."
            : config.NotGroundedBlockMessage;

        if (GetBoolMember(entity, "Swimming") || GetBoolMember(entity, "FeetInLiquid"))
        {
            error = "You must be standing on solid ground, not swimming or standing in liquid, to use /afk.";
            return false;
        }

        if (TryGetVerticalMotion(entity, out double motionY) && Math.Abs(motionY) > Math.Max(0.001, config.MaxVerticalMotionForGroundedAfk))
        {
            return false;
        }

        bool onGround = GetBoolMember(entity, "OnGround") || GetBoolMember(entity, "CollidedVertically");
        if (onGround)
        {
            return true;
        }

        return false;
    }

    private static bool TryGetVerticalMotion(Entity entity, out double motionY)
    {
        motionY = 0;
        object? pos = GetEntityPositionObject(entity);
        object? motion = pos == null ? null : GetMemberValue(pos, "Motion");
        if (motion == null) return false;

        object? y = GetMemberValue(motion, "Y") ?? GetMemberValue(motion, "y");
        switch (y)
        {
            case double d:
                motionY = d;
                return true;
            case float f:
                motionY = f;
                return true;
            case int i:
                motionY = i;
                return true;
            default:
                return false;
        }
    }

    private static bool GetBoolMember(object target, string name)
    {
        object? value = GetMemberValue(target, name);
        return value is bool b && b;
    }

    private AfkCameraPacket BuildCameraPacket(bool enabled)
    {
        return new AfkCameraPacket
        {
            Enabled = enabled,
            CameraDistance = config.CameraDistance,
            CameraHeight = config.CameraHeight,
            CameraPitchDegrees = config.CameraPitchDegrees,
            CameraOrbitSpeedDegreesPerSecond = config.CameraOrbitSpeedDegreesPerSecond,
            TryForceThirdPerson = config.TryForceThirdPerson,
            DebugClientCameraReflection = config.DebugClientCameraReflection,
            EnableClientCamera = config.EnableClientCamera,
            UseCameraOffset = config.UseCameraOffset,
            ForceThirdPersonEveryTick = config.ForceThirdPersonEveryTick,
            PreventCameraGroundClip = config.PreventCameraGroundClip,
            MinimumCameraHeightAbovePlayer = config.MinimumCameraHeightAbovePlayer,
            ThirdPersonForceIntervalMs = config.ThirdPersonForceIntervalMs,
            OnlyOrbitWhenThirdPersonIsActive = config.OnlyOrbitWhenThirdPersonIsActive,
            ReforceThirdPersonIfLost = config.ReforceThirdPersonIfLost,
            ClientCameraTickMs = config.ClientCameraTickMs,
            OrbitByEntityRotation = config.OrbitByEntityRotation,
            WriteCameraYawOnly = config.WriteCameraYawOnly,
            WriteCameraYawPitch = config.WriteCameraYawPitch,
            SmoothOrbitYaw = config.SmoothOrbitYaw,
            MaxYawStepDegreesPerTick = config.MaxYawStepDegreesPerTick,
            UseRenderFrameCamera = config.UseRenderFrameCamera,
            StaticCameraOffset = config.StaticCameraOffset,
            ClampCameraOffsetToLineOfSight = config.ClampCameraOffsetToLineOfSight,
            CameraGroundClearance = config.CameraGroundClearance,
            CameraCollisionSampleStep = config.CameraCollisionSampleStep,
            MinimumCameraDistance = config.MinimumCameraDistance,
            UseVanillaThirdPersonZoom = config.UseVanillaThirdPersonZoom
        };
    }

    private bool DenyCreatureSpawnNearAfkPlayer(EntityProperties type, Vec3d spawnPosition, RuntimeSpawnConditions sc)
    {
        // This delegate is attached only while the player is AFK. The engine calls it when
        // considering runtime creature spawns around this specific player. Returning false
        // makes the AFK player ineligible as a natural creature-spawn anchor without globally
        // disabling entity spawning or interfering with active players elsewhere.
        return false;
    }

    private void TryInstallDamageTrackingPatch()
    {
        if (harmony == null) return;

        try
        {
            MethodBase? target = NewfiesAfkDamagePatch.TargetMethod();
            MethodInfo? postfix = AccessTools.Method(
                typeof(NewfiesAfkDamagePatch),
                nameof(NewfiesAfkDamagePatch.Postfix));

            if (target == null || postfix == null)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] Player damage tracking patch target was not found; combat AFK cooldown may be unavailable.");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception ex)
        {
            sapi?.Logger.Warning(
                "[ServerEssentials/AFK] Damage tracking patch failed safely: {0}: {1}",
                ex.GetType().Name,
                ex.Message);
        }
    }

    private void TryInstallHungerCompatibility()
    {
        if (harmony == null || !config.PauseHungerWhileAfk) return;

        try
        {
            Type? hungerType = AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorHunger");
            if (hungerType == null)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] Vanilla hunger behavior was not found. AFK hunger protection is unavailable.");
                return;
            }

            MethodInfo? reduceSaturation = AccessTools.Method(hungerType, "ReduceSaturation", new[] { typeof(float) });
            MethodInfo? reducePrefix = AccessTools.Method(typeof(NewfiesAfkHungerReductionCompatibilityPatch), nameof(NewfiesAfkHungerReductionCompatibilityPatch.Prefix));

            MethodInfo? slowTick = AccessTools.Method(hungerType, "SlowTick", new[] { typeof(float) });
            MethodInfo? slowTickPrefix = AccessTools.Method(typeof(NewfiesAfkHungerSlowTickCompatibilityPatch), nameof(NewfiesAfkHungerSlowTickCompatibilityPatch.Prefix));

            int patchedMethods = 0;

            if (reduceSaturation != null && reducePrefix != null)
            {
                harmony.Patch(reduceSaturation, prefix: new HarmonyMethod(reducePrefix));
                patchedMethods++;
            }

            if (slowTick != null && slowTickPrefix != null)
            {
                harmony.Patch(slowTick, prefix: new HarmonyMethod(slowTickPrefix));
                patchedMethods++;
            }

            hungerCompatibilityPatched = patchedMethods > 0;
            if (hungerCompatibilityPatched)
            {
                sapi?.Logger.Notification("[ServerEssentials/AFK] Hunger protection active: saturation/nutrition loss and starvation slow-ticks are paused while AFK.");
            }
            else
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] Vanilla hunger behavior was found, but its reduction/slow-tick methods could not be patched.");
            }
        }
        catch (Exception ex)
        {
            hungerCompatibilityPatched = false;
            sapi?.Logger.Warning("[ServerEssentials/AFK] Hunger compatibility patch failed safely: {0}", ex.Message);
        }
    }

    private void TryInstallHydrateOrDiedrateCompatibility()
    {
        if (harmony == null || !config.PauseHydrateOrDiedrateThirstWhileAfk) return;

        try
        {
            // Hydrate or Diedrate is optional. Resolve its behavior dynamically so this mod
            // never gains a hard dependency on HoD and can still load when HoD is absent.
            Type? thirstType = AccessTools.TypeByName("HydrateOrDiedrate.EntityBehaviorThirst");
            if (thirstType == null)
            {
                if (config.DebugLogging)
                {
                    sapi?.Logger.Notification("[ServerEssentials/AFK] Hydrate or Diedrate not detected; AFK thirst protection was not patched.");
                }
                return;
            }

            MethodInfo? onGameTick = AccessTools.Method(thirstType, "OnGameTick", new[] { typeof(float) });
            MethodInfo? prefix = AccessTools.Method(typeof(NewfiesAfkHydrateOrDiedrateCompatibilityPatch), nameof(NewfiesAfkHydrateOrDiedrateCompatibilityPatch.Prefix));

            if (onGameTick == null || prefix == null)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] Hydrate or Diedrate detected, but EntityBehaviorThirst.OnGameTick(float) could not be patched.");
                return;
            }

            harmony.Patch(onGameTick, prefix: new HarmonyMethod(prefix));
            hydrateOrDiedrateCompatibilityPatched = true;
            sapi?.Logger.Notification("[ServerEssentials/AFK] Hydrate or Diedrate compatibility active: thirst decay and dehydration ticks are paused while AFK.");
        }
        catch (Exception ex)
        {
            hydrateOrDiedrateCompatibilityPatched = false;
            sapi?.Logger.Warning("[ServerEssentials/AFK] Hydrate or Diedrate compatibility patch failed safely: {0}", ex.Message);
        }
    }

    internal bool ShouldPauseHungerForAfk(object hungerBehavior)
    {
        return config.PauseHungerWhileAfk && IsBehaviorOwnedByAfkPlayer(hungerBehavior);
    }

    internal bool ShouldPauseHydrateOrDiedrateThirstForAfk(object thirstBehavior)
    {
        return config.PauseHydrateOrDiedrateThirstWhileAfk && IsBehaviorOwnedByAfkPlayer(thirstBehavior);
    }

    private bool IsBehaviorOwnedByAfkPlayer(object behavior)
    {
        try
        {
            object? entityObject = GetMemberValue(behavior, "entity") ?? GetMemberValue(behavior, "Entity");
            if (entityObject is not EntityPlayer playerEntity) return false;

            string? playerUid = playerEntity.Player?.PlayerUID;
            if (string.IsNullOrWhiteSpace(playerUid))
            {
                playerUid = GetMemberValue(playerEntity, "PlayerUID") as string;
            }

            return !string.IsNullOrWhiteSpace(playerUid) && afkPlayers.ContainsKey(playerUid);
        }
        catch (Exception ex)
        {
            if (config.DebugLogging)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] AFK survival-needs ownership check failed open: {0}", ex.Message);
            }

            return false;
        }
    }

    private void TryInstallXSkillsCompatibility()
    {
        if (harmony == null || !config.DisableXSkillsSurvivalXpWhileAfk) return;

        try
        {
            // XSkills Fork keeps the XLib.XLeveling namespace/API. Resolve it dynamically so
            // Server Essentials AFK has no hard dependency on XLib/XSkills and still loads when they are absent.
            Type? playerSkillType = AccessTools.TypeByName("XLib.XLeveling.PlayerSkill");
            if (playerSkillType == null)
            {
                if (config.DebugLogging)
                {
                    sapi?.Logger.Notification("[ServerEssentials/AFK] XSkills/XLib not detected; Survival XP AFK protection not patched.");
                }
                return;
            }

            MethodInfo? addExperience = AccessTools.Method(playerSkillType, "AddExperience", new[] { typeof(float), typeof(bool) });
            if (addExperience == null)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] XSkills/XLib detected, but PlayerSkill.AddExperience(float,bool) was not found. Survival XP AFK protection is unavailable.");
                return;
            }

            MethodInfo? prefix = AccessTools.Method(typeof(NewfiesAfkXSkillsCompatibilityPatch), nameof(NewfiesAfkXSkillsCompatibilityPatch.Prefix));
            if (prefix == null)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] Could not locate the internal XSkills compatibility prefix.");
                return;
            }

            harmony.Patch(addExperience, prefix: new HarmonyMethod(prefix));
            xSkillsCompatibilityPatched = true;
            sapi?.Logger.Notification("[ServerEssentials/AFK] XSkills compatibility active: Survival XP is suppressed while a player is AFK.");
        }
        catch (Exception ex)
        {
            xSkillsCompatibilityPatched = false;
            sapi?.Logger.Warning("[ServerEssentials/AFK] XSkills compatibility patch failed safely: {0}", ex.Message);
        }
    }

    internal bool ShouldSuppressXSkillsSurvivalExperience(object playerSkill, float experience)
    {
        if (!config.DisableXSkillsSurvivalXpWhileAfk || experience <= 0) return false;

        try
        {
            object? skill = GetMemberValue(playerSkill, "Skill");
            if (skill == null) return false;
            string? skillName = GetMemberValue(skill, "Name") as string;
            if (!string.Equals(skillName, "survival", StringComparison.OrdinalIgnoreCase)) return false;

            object? playerSkillSet = GetMemberValue(playerSkill, "PlayerSkillSet");
            object? playerObject = playerSkillSet == null ? null : GetMemberValue(playerSkillSet, "Player");

            string? playerUid = playerObject is IPlayer player
                ? player.PlayerUID
                : playerObject == null ? null : GetMemberValue(playerObject, "PlayerUID") as string;

            if (string.IsNullOrWhiteSpace(playerUid)) return false;
            bool suppress = afkPlayers.ContainsKey(playerUid);

            if (suppress && config.DebugLogging)
            {
                sapi?.Logger.Notification("[ServerEssentials/AFK] Suppressed {0:0.###} XSkills Survival XP for AFK uid={1}.", experience, playerUid);
            }

            return suppress;
        }
        catch (Exception ex)
        {
            // Compatibility should always fail open: an XSkills internal change must never block XP
            // for active players or crash the server.
            if (config.DebugLogging)
            {
                sapi?.Logger.Warning("[ServerEssentials/AFK] XSkills AFK XP check failed open: {0}", ex.Message);
            }
            return false;
        }
    }

    internal void MarkDamaged(EntityPlayer entity, float damage)
    {
        if (sapi == null) return;
        if (damage <= 0) return;

        string uid = entity.PlayerUID;
        if (string.IsNullOrWhiteSpace(uid)) return;

        lastDamageUnixMsByPlayerUid[uid] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (config.DebugLogging)
        {
            sapi.Logger.Notification("[ServerEssentials/AFK] Damage recorded for uid={0}. damage={1}", uid, damage);
        }

        if (config.DisableAfkWhenDamaged && entity.Player is IServerPlayer player && afkPlayers.ContainsKey(uid))
        {
            DisableAfk(player, false);
            player.SendMessage(GlobalConstants.GeneralChatGroup, "AFK mode disabled because you took damage.", EnumChatType.Notification);
        }
    }

    private bool IsCombatCooldownActive(IServerPlayer player, out int secondsRemaining)
    {
        secondsRemaining = 0;

        if (config.CombatCooldownSeconds <= 0) return false;
        if (!lastDamageUnixMsByPlayerUid.TryGetValue(player.PlayerUID, out long lastDamageUnixMs)) return false;

        double elapsedSeconds = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastDamageUnixMs) / 1000.0;
        double remaining = config.CombatCooldownSeconds - elapsedSeconds;

        if (remaining <= 0) return false;

        secondsRemaining = Math.Max(1, (int)Math.Ceiling(remaining));
        return true;
    }

    private string FormatCombatBlockMessage(int secondsRemaining)
    {
        string template = string.IsNullOrWhiteSpace(config.CombatBlockMessage)
            ? "You cannot go AFK for {seconds} more seconds because you were recently damaged."
            : config.CombatBlockMessage;

        return template
            .Replace("{seconds}", secondsRemaining.ToString())
            .Replace("{minutes}", Math.Ceiling(secondsRemaining / 60.0).ToString("0"));
    }

    private static double? TryGetCurrentHealth(EntityPlayer entity)
    {
        try
        {
            EntityBehavior? healthBehavior = entity.GetBehavior("health");
            if (healthBehavior == null) return null;

            object? healthValue = GetMemberValue(healthBehavior, "Health")
                ?? GetMemberValue(healthBehavior, "health")
                ?? GetMemberValue(healthBehavior, "CurrentHealth")
                ?? GetMemberValue(healthBehavior, "currentHealth");

            if (healthValue is float f) return f;
            if (healthValue is double d) return d;
            if (healthValue is int i) return i;
            if (healthValue is long l) return l;

            return null;
        }
        catch
        {
            return null;
        }
    }

    // The lock tick runs several times a second per AFK player and touches ~30 members by name.
    // Member resolution is cached per (type, name); misses are cached too.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), MemberInfo?> readMemberCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), (PropertyInfo? Prop, FieldInfo? Field)> writeMemberCache = new();

    private static MemberInfo? ResolveReadMember(Type targetType, string name)
    {
        return readMemberCache.GetOrAdd((targetType, name), static key =>
        {
            Type? type = key.Item1;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            while (type != null)
            {
                PropertyInfo? prop = type.GetProperty(key.Item2, flags);
                if (prop != null) return prop;

                FieldInfo? field = type.GetField(key.Item2, flags);
                if (field != null) return field;

                type = type.BaseType;
            }

            return null;
        });
    }

    private static object? GetMemberValue(object target, string name)
    {
        return ResolveReadMember(target.GetType(), name) switch
        {
            PropertyInfo prop => prop.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => null
        };
    }

    private bool TryHandleAutoKick(IServerPlayer player, AfkPlayerState state)
    {
        if (!config.AutoKickAfkEnabled || config.AutoKickAfterMinutes <= 0)
        {
            return false;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (state.EnteredAfkUnixMs <= 0)
        {
            state.EnteredAfkUnixMs = now;
            return false;
        }

        double kickAfterMs = config.AutoKickAfterMinutes * 60_000.0;
        double elapsedMs = Math.Max(0, now - state.EnteredAfkUnixMs);
        double remainingSeconds = (kickAfterMs - elapsedMs) / 1000.0;

        if (remainingSeconds <= 0)
        {
            string reason = string.IsNullOrWhiteSpace(config.AutoKickReason)
                ? "Kicked for being AFK too long."
                : config.AutoKickReason;

            if (config.DebugLogging)
            {
                sapi?.Logger.Notification(
                    "[ServerEssentials/AFK] Auto-kicking {0} after {1:0.##} minutes AFK.",
                    player.PlayerName,
                    config.AutoKickAfterMinutes
                );
            }

            // Always restore the return gamemode before an AFK kick so Spectator can never
            // be persisted into the player's next login.
            RestoreAfkStateForDisconnect(player);
            player.Disconnect(reason);
            return true;
        }

        int warningWindow = Math.Max(0, config.AutoKickWarningSeconds);
        if (warningWindow > 0 && !state.AutoKickWarningSent && remainingSeconds <= warningWindow)
        {
            state.AutoKickWarningSent = true;
            int secondsRemaining = Math.Max(1, (int)Math.Ceiling(remainingSeconds));
            string message = FormatAutoKickWarning(secondsRemaining);
            player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
        }

        return false;
    }

    private string FormatAutoKickWarning(int secondsRemaining)
    {
        string template = string.IsNullOrWhiteSpace(config.AutoKickWarningMessage)
            ? "You will be kicked in {seconds} seconds for being AFK too long."
            : config.AutoKickWarningMessage;

        return template
            .Replace("{seconds}", secondsRemaining.ToString())
            .Replace("{minutes}", Math.Ceiling(secondsRemaining / 60.0).ToString("0"));
    }

    private void OnServerLockTick(float dt)
    {
        if (sapi == null) return;

        try
        {
            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
            {
                if (player?.Entity == null) continue;
                if (!afkPlayers.TryGetValue(player.PlayerUID, out AfkPlayerState? state)) continue;

                if (TryHandleAutoKick(player, state))
                {
                    continue;
                }

                if (config.ZeroControlsEveryLockTick)
                {
                    ZeroControlsAndMotion(player.Entity);
                }

                if (!TryReadEntityPosition(player.Entity, out AfkPlayerState current))
                {
                    continue;
                }

                double dx = current.X - state.X;
                double dy = current.Y - state.Y;
                double dz = current.Z - state.Z;
                double distSq = dx * dx + dy * dy + dz * dz;
                double tolerance = Math.Max(0.01, config.LockDistanceTolerance);

                if (distSq > tolerance * tolerance)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    lastCorrectionUnixMsByPlayerUid.TryGetValue(player.PlayerUID, out long lastCorrection);

                    if (now - lastCorrection >= Math.Max(50, config.DriftCorrectionIntervalMs))
                    {
                        lastCorrectionUnixMsByPlayerUid[player.PlayerUID] = now;
                        ZeroControlsAndMotion(player.Entity);
                        TeleportEntityToLockedPos(player.Entity, state);
                    }
                }
                else if (config.RewritePositionWhenWithinTolerance)
                {
                    // Off by default: constantly rewriting the transform can fight the client animation system.
                    TryWriteEntityPosition(player.Entity, state);
                }

                // Do this after any drift correction/position rewrite so those operations cannot restore the
                // saved entry yaw over the spin. Entity position/yaw is normal server-replicated state, so
                // clients do not need Server Essentials installed to see the AFK player rotate.
                ApplyServerAfkSpin(player.Entity, state);
            }
        }
        catch (Exception ex)
        {
            // Never let AFK locking spam enough exceptions to kill the server.
            sapi.Logger.Error("[ServerEssentials/AFK] Lock tick failed once and was safely ignored: {0}", ex);
        }
    }

    private static void TeleportEntityToLockedPos(EntityPlayer entity, AfkPlayerState state)
    {
        TryWriteEntityPosition(entity, state);
        SetMemberIfExists(entity, "IsTeleport", true);
        entity.TeleportToDouble(state.X, state.Y, state.Z);
        TryWriteEntityPosition(entity, state);
    }

    private void ApplyServerAfkSpin(EntityPlayer entity, AfkPlayerState state)
    {
        if (!config.EnableServerEntitySpin || entity == null || state.EnteredAfkUnixMs <= 0) return;

        double elapsedSeconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.EnteredAfkUnixMs) / 1000.0;
        double yaw = state.Yaw + elapsedSeconds * config.CameraOrbitSpeedDegreesPerSecond * (Math.PI / 180.0);
        yaw %= Math.PI * 2.0;
        if (yaw < 0) yaw += Math.PI * 2.0;
        float serverYaw = (float)yaw;

        // 1.22.x has moved pose members between fields/properties across builds. Update every
        // authoritative/server-visible pose object we can find rather than binding to one layout.
        // PreviousServerPos is deliberately untouched: the engine uses the Pos-vs-PreviousServerPos
        // delta to decide that an entity position packet (including yaw) needs to be replicated.
        WriteViewYawToPosObject(GetMemberValue(entity, "Pos"), serverYaw);
        WriteViewYawToPosObject(GetMemberValue(entity, "SidedPos"), serverYaw);
        WriteViewYawToPosObject(GetMemberValue(entity, "ServerPos"), serverYaw);
        SetMemberIfExists(entity, "Yaw", serverYaw);
        SetMemberIfExists(entity, "BodyYaw", serverYaw);
    }

    private static bool TryReadEntityPosition(Entity entity, out AfkPlayerState state)
    {
        state = new AfkPlayerState();
        object? pos = GetEntityPositionObject(entity);
        if (pos == null) return false;

        state.X = GetDoubleMember(pos, "X");
        state.Y = GetDoubleMember(pos, "Y");
        state.Z = GetDoubleMember(pos, "Z");
        state.Yaw = GetFloatMember(pos, "Yaw");
        state.Pitch = GetFloatMember(pos, "Pitch");
        state.Roll = GetFloatMember(pos, "Roll");
        return true;
    }

    private static bool TryWriteEntityPosition(Entity entity, AfkPlayerState state)
    {
        object? pos = GetEntityPositionObject(entity);
        if (pos == null) return false;

        SetMemberIfExists(pos, "X", state.X);
        SetMemberIfExists(pos, "Y", state.Y);
        SetMemberIfExists(pos, "Z", state.Z);
        SetMemberIfExists(pos, "Yaw", state.Yaw);
        // Do not lock Pitch/Roll. In 1.22.2 spectator/client camera, repeatedly writing pitch can
        // pin the player's actual view straight up/down. AFK lock only needs XYZ plus yaw.
        // SetMemberIfExists(pos, "Pitch", state.Pitch);
        // SetMemberIfExists(pos, "Roll", state.Roll);
        return true;
    }

    private static object? GetEntityPositionObject(Entity entity)
    {
        // 1.22 changed these from fields to properties. Direct compiled access can JIT-crash
        // when the mod was compiled against a different minor API. Reflection supports both.
        return GetMemberValue(entity, "Pos")
            ?? GetMemberValue(entity, "SidedPos")
            ?? GetMemberValue(entity, "ServerPos");
    }

    private static double GetDoubleMember(object target, string name)
    {
        object? value = GetMemberValue(target, name);
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => 0d
        };
    }

    private static float GetFloatMember(object target, string name)
    {
        object? value = GetMemberValue(target, name);
        return value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            _ => 0f
        };
    }

    private static void ZeroControlsAndMotion(EntityPlayer entity)
    {
        object? pos = GetEntityPositionObject(entity);
        object? motion = pos == null ? null : GetMemberValue(pos, "Motion");

        if (motion != null)
        {
            InvokeMethodIfExists(motion, "Set", 0d, 0d, 0d);
            InvokeMethodIfExists(motion, "Set", 0f, 0f, 0f);
        }

        entity.Controls?.StopAllMovement();
        entity.ServerControls?.StopAllMovement();

        if (entity.Controls != null)
        {
            entity.Controls.FlyVector.Set(0, 0, 0);
            entity.Controls.WalkVector.Set(0, 0, 0);
        }

        if (entity.ServerControls != null)
        {
            entity.ServerControls.FlyVector.Set(0, 0, 0);
            entity.ServerControls.WalkVector.Set(0, 0, 0);
        }
    }

    private static bool InvokeMethodIfExists(object target, string name, params object[] args)
    {
        MethodInfo? method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(methodInfo => methodInfo.Name == name && methodInfo.GetParameters().Length == args.Length);

        if (method == null) return false;

        try
        {
            method.Invoke(target, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        if (player == null) return;
        cameraCapablePlayers.Remove(player.PlayerUID);

        // Important: if the player logs out while AFK, restore the saved/return gamemode
        // before removing their AFK state. Otherwise the server can persist Spectator
        // and they may log back in still in spectator mode.
        if (config.RestoreGameModeOnDisconnect)
        {
            RestoreAfkStateForDisconnect(player);
        }
        else
        {
            if (player.Entity != null)
            {
                player.Entity.OnCanSpawnNearby -= DenyCreatureSpawnNearAfkPlayer;
            }
            afkPlayers.Remove(player.PlayerUID);
        }

        lastDamageUnixMsByPlayerUid.Remove(player.PlayerUID);
        lastCorrectionUnixMsByPlayerUid.Remove(player.PlayerUID);
    }

    private void RestoreAfkStateForDisconnect(IServerPlayer player)
    {
        if (!afkPlayers.TryGetValue(player.PlayerUID, out AfkPlayerState? state)) return;

        if (player.Entity != null)
        {
            player.Entity.OnCanSpawnNearby -= DenyCreatureSpawnNearAfkPlayer;
        }

        afkPlayers.Remove(player.PlayerUID);

        try
        {
            player.WorldData.CurrentGameMode = GetReturnGameMode(state);
            player.BroadcastPlayerData();

            if (config.DebugLogging && sapi != null)
            {
                sapi.Logger.Notification(
                    "[ServerEssentials/AFK] Restored gamemode for {0} on disconnect to {1}.",
                    player.PlayerName,
                    player.WorldData.CurrentGameMode
                );
            }
        }
        catch (Exception ex)
        {
            sapi?.Logger.Warning(
                "[ServerEssentials/AFK] Failed to restore gamemode for {0} on disconnect: {1}",
                player.PlayerName,
                ex.Message
            );
        }
    }

    private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        if (player == null) return;
        if (player?.Entity != null) MarkDamaged(player.Entity, 1f);
        if (player != null) DisableAfk(player, false);
    }

    private AfkCameraPacket ApplyLocalClientCameraConfig(AfkCameraPacket packet)
    {
        // Only the Enabled flag is trusted from the server. Everything visual is local client config.
        packet.EnableClientCamera = config.EnableClientCamera;
        packet.CameraDistance = config.CameraDistance;
        packet.CameraHeight = config.CameraHeight;
        packet.CameraPitchDegrees = config.CameraPitchDegrees;
        packet.CameraOrbitSpeedDegreesPerSecond = config.CameraOrbitSpeedDegreesPerSecond;
        packet.TryForceThirdPerson = config.TryForceThirdPerson;
        packet.DebugClientCameraReflection = config.DebugClientCameraReflection;
        packet.UseCameraOffset = config.UseCameraOffset;
        packet.UseVanillaThirdPersonZoom = config.UseVanillaThirdPersonZoom;
        packet.ForceThirdPersonEveryTick = config.ForceThirdPersonEveryTick;
        packet.PreventCameraGroundClip = config.PreventCameraGroundClip;
        packet.MinimumCameraHeightAbovePlayer = config.MinimumCameraHeightAbovePlayer;
        packet.ThirdPersonForceIntervalMs = config.ThirdPersonForceIntervalMs;
        packet.OnlyOrbitWhenThirdPersonIsActive = config.OnlyOrbitWhenThirdPersonIsActive;
        packet.ReforceThirdPersonIfLost = config.ReforceThirdPersonIfLost;
        packet.ClientCameraTickMs = config.ClientCameraTickMs;
        packet.OrbitByEntityRotation = config.OrbitByEntityRotation;
        packet.WriteCameraYawOnly = config.WriteCameraYawOnly;
        packet.WriteCameraYawPitch = config.WriteCameraYawPitch;
        packet.SmoothOrbitYaw = config.SmoothOrbitYaw;
        packet.MaxYawStepDegreesPerTick = config.MaxYawStepDegreesPerTick;
        packet.UseRenderFrameCamera = config.UseRenderFrameCamera;
        packet.StaticCameraOffset = config.StaticCameraOffset;
        packet.ClampCameraOffsetToLineOfSight = config.ClampCameraOffsetToLineOfSight;
        packet.CameraGroundClearance = config.CameraGroundClearance;
        packet.CameraCollisionSampleStep = config.CameraCollisionSampleStep;
        packet.MinimumCameraDistance = config.MinimumCameraDistance;
        return packet;
    }

    private void OnAfkCameraPacket(AfkCameraPacket packet)
    {
        if (capi == null) return;

        // Server only authorizes/toggles AFK camera. All camera tuning is client-side local
        // so players can adjust distance/smoothness without requiring a server config change.
        packet = ApplyLocalClientCameraConfig(packet);
        currentCameraPacket = packet;

        if (!packet.EnableClientCamera)
        {
            StopClientAfkCamera(restoreViewAngles: false, showMessage: false);
            return;
        }

        if (packet.Enabled)
        {
            previousCameraPitch = capi.World.Player.CameraPitch;
            previousCameraYaw = capi.World.Player.CameraYaw;
            previousCameraMode = GetCurrentMainCameraMode();
            previousCameraModeWasCaptured = previousCameraMode != null;
            orbitAngleRadians = capi.World.Player.CameraYaw;
            orbitBaseYawRadians = orbitAngleRadians;
            lastAppliedOrbitYaw = capi.World.Player.CameraYaw;
            clientCameraStartMs = capi.ElapsedMilliseconds;
            clientAfkCameraEnabled = true;
            lastThirdPersonForceMs = 0;

            // Force third-person once on enable only. Re-forcing while orbiting is what causes flicker on 1.22.x.
            if (packet.TryForceThirdPerson)
            {
                TryForceThirdPersonCamera();
                lastThirdPersonForceMs = capi.ElapsedMilliseconds;
            }

            RestoreCameraOffset();
            EnsureClientCameraUpdaterRegistered();
            capi.World.Player.ShowChatNotification("AFK camera enabled.");
            return;
        }

        StopClientAfkCamera(restoreViewAngles: true, showMessage: true);
    }

    public double RenderOrder => 0.5;

    public int RenderRange => 999999;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        OnClientCameraTick(deltaTime);
    }


    private void OnClientCameraTick(float dt)
    {
        if (!clientAfkCameraEnabled || capi == null) return;
        if (capi.World?.Player == null) return;

        bool cameraIsThirdPerson = IsCurrentlyThirdPersonCamera();

        // Important: Do not re-force third person just because detection says it is not active.
        // On some 1.22.x clients the detection can disagree with the actual camera, causing rapid mode tug-of-war.
        if (currentCameraPacket.TryForceThirdPerson)
        {
            int intervalMs = Math.Max(100, currentCameraPacket.ThirdPersonForceIntervalMs);
            bool shouldForceNow = currentCameraPacket.ForceThirdPersonEveryTick
                || (currentCameraPacket.ReforceThirdPersonIfLost && !cameraIsThirdPerson && capi.ElapsedMilliseconds - lastThirdPersonForceMs >= intervalMs);

            if (shouldForceNow)
            {
                TryForceThirdPersonCamera();
                lastThirdPersonForceMs = capi.ElapsedMilliseconds;
                cameraIsThirdPerson = IsCurrentlyThirdPersonCamera();
            }
        }

        // Keep the cinematic orbit. Default no longer blocks orbit on third-person detection because that detection
        // is not reliable enough on every 1.22.x client and can indirectly cause flicker.
        if (!currentCameraPacket.OnlyOrbitWhenThirdPersonIsActive || cameraIsThirdPerson)
        {
            double speedRadians = currentCameraPacket.CameraOrbitSpeedDegreesPerSecond * GameMath.DEG2RAD;

            // Use wall-clock time instead of accumulating game-tick dt. This prevents tiny tick timing
            // variations from showing up as visible orbit stutter. Render-frame mode calls this every frame.
            double elapsedSeconds = Math.Max(0, (capi.ElapsedMilliseconds - clientCameraStartMs) / 1000.0);
            orbitAngleRadians = orbitBaseYawRadians + speedRadians * elapsedSeconds;

            float targetYaw = (float)orbitAngleRadians;
            float yaw = targetYaw;

            if (currentCameraPacket.SmoothOrbitYaw)
            {
                // Frame-rate independent clamp. At 60 FPS this is usually larger than the normal yaw movement,
                // so it smooths spikes without quantizing the regular orbit.
                double dtScale = Math.Max(0.001, dt / (1.0 / 60.0));
                float maxStep = (float)(Math.Max(0.1, currentCameraPacket.MaxYawStepDegreesPerTick) * dtScale * GameMath.DEG2RAD);
                yaw = StepAngleRadians(lastAppliedOrbitYaw, targetYaw, maxStep);
                lastAppliedOrbitYaw = yaw;
            }

            float pitch = (float)(currentCameraPacket.CameraPitchDegrees * GameMath.DEG2RAD);

            // Do not write CameraPitch by default. On 1.22.x camera pitch/entity pitch can fight the
            // vanilla third-person camera or pin the actual player look direction straight up/down.
            // The default auto-orbit uses yaw only, which gives rotation without touching vertical look.
            if (currentCameraPacket.OrbitByEntityRotation)
            {
                ApplyEntityViewRotation(yaw);
            }

            // Safer auto-orbit mode: write camera yaw only. Do not write pitch here, because on
            // 1.22.x that can pin the actual player view straight up/down or fight vanilla third-person.
            if (currentCameraPacket.WriteCameraYawOnly || currentCameraPacket.WriteCameraYawPitch)
            {
                capi.World.Player.CameraYaw = yaw;
            }

            if (currentCameraPacket.WriteCameraYawPitch)
            {
                capi.World.Player.CameraPitch = pitch;
            }

            if (currentCameraPacket.UseVanillaThirdPersonZoom)
            {
                TryApplyVanillaThirdPersonDistance(currentCameraPacket.CameraDistance);
            }

            if (currentCameraPacket.UseCameraOffset)
            {
                ApplyCameraOffset(currentCameraPacket.CameraDistance, currentCameraPacket.CameraHeight, orbitAngleRadians, currentCameraPacket.StaticCameraOffset);
            }
        }
    }

    private static float StepAngleRadians(float current, float target, float maxStep)
    {
        double diff = target - current;
        while (diff > Math.PI) diff -= Math.PI * 2;
        while (diff < -Math.PI) diff += Math.PI * 2;

        if (Math.Abs(diff) <= maxStep) return target;
        return current + (float)(Math.Sign(diff) * maxStep);
    }

    private void EnsureClientCameraUpdaterRegistered()
    {
        if (capi == null) return;

        if (currentCameraPacket.UseRenderFrameCamera && !renderFrameCameraRegistered)
        {
            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "newfies-suite-afk-camera");
            renderFrameCameraRegistered = true;
            return;
        }

        if (!currentCameraPacket.UseRenderFrameCamera && clientCameraTickId == 0)
        {
            int intervalMs = Math.Max(5, currentCameraPacket.ClientCameraTickMs <= 0 ? 16 : currentCameraPacket.ClientCameraTickMs);
            clientCameraTickId = capi.Event.RegisterGameTickListener(OnClientCameraTick, intervalMs);
        }
    }

    private void StopClientAfkCamera(bool restoreViewAngles, bool showMessage)
    {
        if (capi == null) return;

        clientAfkCameraEnabled = false;
        if (clientCameraTickId != 0)
        {
            capi.Event.UnregisterGameTickListener(clientCameraTickId);
            clientCameraTickId = 0;
        }

        if (renderFrameCameraRegistered)
        {
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Before);
            renderFrameCameraRegistered = false;
        }

        RestoreCameraOffset();

        if (restoreViewAngles && capi.World?.Player != null)
        {
            capi.World.Player.CameraPitch = previousCameraPitch;
            capi.World.Player.CameraYaw = previousCameraYaw;
        }

        if (previousCameraModeWasCaptured && previousCameraMode != null)
        {
            SetMainCameraMode(previousCameraMode);
        }

        previousCameraMode = null;
        previousCameraModeWasCaptured = false;

        if (showMessage && capi.World?.Player != null)
        {
            capi.World.Player.ShowChatNotification("AFK camera disabled.");
        }
    }

    private void ApplyEntityViewRotation(float yaw)
    {
        if (capi?.World?.Player?.Entity == null) return;

        try
        {
            object entity = capi.World.Player.Entity;

            // 1.22.x moved several entity position members between fields/properties. Use reflection
            // and try all relevant pose objects. Avoid touching CameraYaw/CameraPitch unless explicitly enabled.
            object? sidedPos = GetMemberValue(entity, "SidedPos");
            object? pos = GetMemberValue(entity, "Pos");
            object? serverPos = GetMemberValue(entity, "ServerPos");

            bool wrote = false;
            wrote |= WriteViewYawToPosObject(sidedPos, yaw);
            wrote |= WriteViewYawToPosObject(pos, yaw);
            wrote |= WriteViewYawToPosObject(serverPos, yaw);

            // Some builds expose view yaw directly on the entity or controls. Try yaw only.
            // Do not write Pitch/HeadPitch here; that was causing the player to be locked looking straight up.
            wrote |= SetMemberIfExists(entity, "Yaw", yaw);
            wrote |= SetMemberIfExists(entity, "BodyYaw", yaw);

            object? controls = GetMemberValue(entity, "Controls");
            if (controls != null)
            {
                wrote |= SetMemberIfExists(controls, "Yaw", yaw);
            }

            if (!wrote && currentCameraPacket.DebugClientCameraReflection)
            {
                capi.Logger.Notification("[ServerEssentials/AFK] Entity orbit rotation found no writable yaw members.");
            }
        }
        catch (Exception ex)
        {
            if (currentCameraPacket.DebugClientCameraReflection)
            {
                capi.Logger.Warning("[ServerEssentials/AFK] Entity orbit rotation failed: {0}", ex.Message);
            }
        }
    }

    private static bool WriteViewYawToPosObject(object? pos, float yaw)
    {
        if (pos == null) return false;

        bool wrote = false;
        wrote |= SetMemberIfExists(pos, "Yaw", yaw);
        wrote |= SetMemberIfExists(pos, "HeadYaw", yaw);
        wrote |= SetMemberIfExists(pos, "BodyYaw", yaw);
        return wrote;
    }

    private void TryApplyVanillaThirdPersonDistance(double distance)
    {
        if (capi == null) return;

        // Prefer vanilla third-person distance/zoom internals when present. This avoids CameraOffset,
        // which can clip into terrain and reveal underground/cave space. All writes are best-effort.
        float dist = (float)Math.Max(1.0, distance);
        bool wrote = false;

        object? mainCamera = GetMainCameraObject();
        wrote |= TrySetCameraDistanceMembers(mainCamera, dist);
        wrote |= TrySetCameraDistanceMembers(capi.Render, dist);
        wrote |= TrySetCameraDistanceMembers(capi.World?.Player, dist);
        wrote |= TrySetCameraDistanceMembers(capi.World?.Player?.Entity, dist);

        // Some builds store camera distance/zoom under settings dictionaries. Use reflection only.
        object? settings = GetMemberValue(capi, "Settings") ?? GetMemberValue(capi, "settings");
        wrote |= TrySetDistanceSetting(settings, dist);

        if (!wrote && currentCameraPacket.DebugClientCameraReflection)
        {
            capi.Logger.Warning("[ServerEssentials/AFK] Could not find a vanilla third-person distance member. CameraDistance will not work unless UseCameraOffset is enabled, but CameraOffset may clip into terrain.");
            LogLikelyCameraMembers(mainCamera, "MainCamera");
            LogLikelyCameraMembers(capi.Render, "Render");
            LogLikelyCameraMembers(capi.World?.Player, "Player");
        }
    }

    private static bool TrySetCameraDistanceMembers(object? target, float distance)
    {
        if (target == null) return false;

        bool wrote = false;
        string[] names =
        {
            "ThirdPersonCameraDistance", "thirdPersonCameraDistance",
            "ThirdPersonDistance", "thirdPersonDistance",
            "ThirdPersonZoom", "thirdPersonZoom",
            "CameraDistance", "cameraDistance",
            "TargetCameraDistance", "targetCameraDistance",
            "DesiredCameraDistance", "desiredCameraDistance",
            "CameraZoom", "cameraZoom",
            "TargetCameraZoom", "targetCameraZoom",
            "tpCameraDistance", "TpCameraDistance",
            "tpCameraZoom", "TpCameraZoom"
        };

        foreach (string name in names)
        {
            wrote |= SetMemberIfExists(target, name, distance);
        }

        return wrote;
    }

    private static bool TrySetDistanceSetting(object? settings, float distance)
    {
        if (settings == null) return false;

        bool wrote = false;
        string[] keys =
        {
            "thirdPersonCameraDistance", "ThirdPersonCameraDistance",
            "thirdPersonDistance", "ThirdPersonDistance",
            "tpCameraDistance", "TpCameraDistance",
            "cameraDistance", "CameraDistance"
        };

        object? floats = GetMemberValue(settings, "Float") ?? GetMemberValue(settings, "float") ?? GetMemberValue(settings, "Floats") ?? GetMemberValue(settings, "floats");
        if (floats != null)
        {
            foreach (string key in keys)
            {
                wrote |= TrySetIndexerValue(floats, key, distance);
            }
        }

        foreach (string key in keys)
        {
            wrote |= TryInvokeSetMethod(settings, key, distance);
        }

        return wrote;
    }

    private static bool TrySetIndexerValue(object target, string key, float value)
    {
        try
        {
            PropertyInfo? indexer = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(prop => prop.GetIndexParameters().Length == 1 && prop.CanWrite);
            if (indexer == null) return false;

            ParameterInfo param = indexer.GetIndexParameters()[0];
            object index = param.ParameterType == typeof(string) ? key : key;
            indexer.SetValue(target, ConvertValue(value, indexer.PropertyType), new[] { index });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeSetMethod(object target, string key, float value)
    {
        try
        {
            MethodInfo? method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (!m.Name.Contains("Set", StringComparison.OrdinalIgnoreCase)) return false;
                    ParameterInfo[] ps = m.GetParameters();
                    return ps.Length == 2 && ps[0].ParameterType == typeof(string);
                });

            if (method == null) return false;
            ParameterInfo[] parameters = method.GetParameters();
            object second = ConvertValue(value, parameters[1].ParameterType);
            method.Invoke(target, new object[] { key, second });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void LogLikelyCameraMembers(object? target, string label)
    {
        if (capi == null || target == null) return;
        try
        {
            Type type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            IEnumerable<string> props = type.GetProperties(flags)
                .Where(p => p.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                .Select(p => "property " + p.Name + ":" + p.PropertyType.Name);
            IEnumerable<string> fields = type.GetFields(flags)
                .Where(f => f.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                .Select(f => "field " + f.Name + ":" + f.FieldType.Name);
            capi.Logger.Notification("[ServerEssentials/AFK] {0} likely camera members: {1}", label, string.Join(", ", props.Concat(fields).Take(40)));
        }
        catch
        {
            // debug only
        }
    }

    private void ApplyCameraOffset(double distance, double height, double yawRadians, bool staticOffset)
    {
        if (capi == null) return;

        object? offset = capi.Render?.CameraOffset;
        if (offset == null) return;

        // CameraOffset is a ModelTransform. Its concrete fields/properties have shifted between versions,
        // so use reflection and quietly fail rather than crashing the client.
        // Distance only works when CameraOffset is enabled. By default this offset orbits in world space
        // and yaw points the camera back around the player. If StaticCameraOffset is enabled, it simply
        // pulls the camera back/up on a fixed axis for testing.
        double x = staticOffset ? 0 : Math.Sin(yawRadians) * distance;
        double z = staticOffset ? -distance : Math.Cos(yawRadians) * distance;
        double y = height;
        if (currentCameraPacket.PreventCameraGroundClip)
        {
            y = Math.Max(y, currentCameraPacket.MinimumCameraHeightAbovePlayer);
        }

        bool wrote = false;

        // CameraOffset is a ModelTransform. In 1.22.x this should make CameraDistance/CameraHeight
        // actually affect the vanilla camera. Try the common ModelTransform members and a few
        // lowercase/internal variants so the distance path survives minor API shifts.
        wrote |= SetMemberIfExists(offset, "Translation", new Vec3f((float)x, (float)y, (float)z));
        wrote |= SetMemberIfExists(offset, "translation", new Vec3f((float)x, (float)y, (float)z));
        wrote |= SetMemberIfExists(offset, "TranslationX", (float)x);
        wrote |= SetMemberIfExists(offset, "TranslationY", (float)y);
        wrote |= SetMemberIfExists(offset, "TranslationZ", (float)z);
        wrote |= SetMemberIfExists(offset, "translationX", (float)x);
        wrote |= SetMemberIfExists(offset, "translationY", (float)y);
        wrote |= SetMemberIfExists(offset, "translationZ", (float)z);
        wrote |= SetMemberIfExists(offset, "Origin", new Vec3f(0, 0, 0));
        wrote |= SetMemberIfExists(offset, "origin", new Vec3f(0, 0, 0));

        if (!wrote && currentCameraPacket.DebugClientCameraReflection)
        {
            capi.Logger.Warning("[ServerEssentials/AFK] CameraOffset distance write failed. CameraDistance cannot work until the transform member name is found.");
        }

        if (currentCameraPacket.DebugClientCameraReflection)
        {
            capi.Logger.Notification("[ServerEssentials/AFK] Applied camera offset x={0:0.00}, y={1:0.00}, z={2:0.00}", x, y, z);
        }
    }

    private void RestoreCameraOffset()
    {
        if (capi == null) return;

        object? offset = capi.Render?.CameraOffset;
        if (offset == null) return;

        SetMemberIfExists(offset, "Translation", new Vec3f(0, 0, 0));
        SetMemberIfExists(offset, "translation", new Vec3f(0, 0, 0));
        SetMemberIfExists(offset, "TranslationX", 0f);
        SetMemberIfExists(offset, "TranslationY", 0f);
        SetMemberIfExists(offset, "TranslationZ", 0f);
        SetMemberIfExists(offset, "translationX", 0f);
        SetMemberIfExists(offset, "translationY", 0f);
        SetMemberIfExists(offset, "translationZ", 0f);
    }

    private void TryForceThirdPersonCamera()
    {
        if (capi == null) return;

        try
        {
            object player = capi.World.Player;
            object? mainCamera = GetMainCameraObject();

            // Best path: in current client internals the real camera mode lives on ClientMain.MainCamera.
            // Setting that avoids the vanilla camera fighting our yaw/pitch every frame.
            if (mainCamera != null)
            {
                Type? enumType = GetCameraModeMemberType(mainCamera);
                object? thirdPersonValue = enumType == null
                    ? FindEnumValue("Vintagestory.API.Client.EnumCameraMode", "ThirdPerson", "Overhead", "ThirdPersonOverhead", "ThirdPersonBack", "ThirdPersonFront")
                    : FindEnumValue(enumType, "ThirdPerson", "Overhead", "ThirdPersonOverhead", "ThirdPersonBack", "ThirdPersonFront");

                if (thirdPersonValue != null && SetMainCameraMode(thirdPersonValue))
                {
                    return;
                }
            }

            object? fallbackThirdPerson = FindEnumValue("Vintagestory.API.Client.EnumCameraMode", "ThirdPerson", "Overhead", "ThirdPersonOverhead", "ThirdPersonBack", "ThirdPersonFront");
            if (fallbackThirdPerson != null)
            {
                SetMemberIfExists(player, "CameraMode", fallbackThirdPerson);
                SetMemberIfExists(player, "cameraMode", fallbackThirdPerson);
                SetMemberIfExists(player, "cameraModeInternal", fallbackThirdPerson);
                SetMemberIfExists(player, "<CameraMode>k__BackingField", fallbackThirdPerson);
            }
        }
        catch (Exception ex)
        {
            if (currentCameraPacket.DebugClientCameraReflection)
            {
                capi.Logger.Warning("[ServerEssentials/AFK] Could not force third-person camera: {0}", ex.Message);
            }
        }
    }

    private bool IsCurrentlyThirdPersonCamera()
    {
        if (capi == null) return false;

        object? renderType = GetMemberValue(capi.Render, "CameraType");
        if (IsThirdPersonMode(renderType)) return true;

        object? playerEntity = capi.World?.Player?.Entity;
        object? entityMode = playerEntity == null ? null : GetMemberValue(playerEntity, "CameraMode");
        if (IsThirdPersonMode(entityMode)) return true;

        object? mainMode = GetCurrentMainCameraMode();
        return IsThirdPersonMode(mainMode);
    }

    private static bool IsThirdPersonMode(object? mode)
    {
        if (mode == null) return false;
        string name = mode.ToString() ?? string.Empty;
        return name.Contains("Third", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Overhead", StringComparison.OrdinalIgnoreCase);
    }

    private object? GetMainCameraObject()
    {
        if (capi == null) return null;
        object? world = capi.World;
        if (world == null) return null;

        return GetMemberValue(world, "MainCamera")
            ?? GetMemberValue(world, "mainCamera")
            ?? GetMemberValue(capi, "MainCamera")
            ?? GetMemberValue(capi, "mainCamera");
    }

    private object? GetCurrentMainCameraMode()
    {
        object? camera = GetMainCameraObject();
        if (camera == null) return null;

        return GetMemberValue(camera, "CameraMode")
            ?? GetMemberValue(camera, "cameraMode")
            ?? GetMemberValue(camera, "mode");
    }

    private bool SetMainCameraMode(object modeValue)
    {
        object? camera = GetMainCameraObject();
        if (camera == null) return false;

        return SetMemberIfExists(camera, "CameraMode", modeValue)
            || SetMemberIfExists(camera, "cameraMode", modeValue)
            || SetMemberIfExists(camera, "mode", modeValue)
            || SetMemberIfExists(camera, "<CameraMode>k__BackingField", modeValue);
    }

    private static Type? GetCameraModeMemberType(object camera)
    {
        Type type = camera.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? prop = type.GetProperty("CameraMode", flags) ?? type.GetProperty("cameraMode", flags) ?? type.GetProperty("mode", flags);
        if (prop != null) return prop.PropertyType;

        FieldInfo? field = type.GetField("CameraMode", flags) ?? type.GetField("cameraMode", flags) ?? type.GetField("mode", flags) ?? type.GetField("<CameraMode>k__BackingField", flags);
        return field?.FieldType;
    }

    private static object? FindEnumValue(Type enumType, params string[] preferredNames)
    {
        if (!enumType.IsEnum) return null;

        foreach (string name in preferredNames)
        {
            if (Enum.IsDefined(enumType, name))
            {
                return Enum.Parse(enumType, name);
            }
        }

        foreach (object value in Enum.GetValues(enumType))
        {
            string enumName = value.ToString() ?? string.Empty;
            if (enumName.Contains("Third", StringComparison.OrdinalIgnoreCase)
                || enumName.Contains("Overhead", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static object? FindEnumValue(string enumFullName, params string[] preferredNames)
    {
        Type? enumType = Type.GetType(enumFullName + ", VintagestoryAPI") ?? Type.GetType(enumFullName);
        if (enumType == null || !enumType.IsEnum) return null;

        foreach (string name in preferredNames)
        {
            if (Enum.IsDefined(enumType, name))
            {
                return Enum.Parse(enumType, name);
            }
        }

        foreach (object value in Enum.GetValues(enumType))
        {
            string enumName = value.ToString() ?? string.Empty;
            if (enumName.Contains("Third", StringComparison.OrdinalIgnoreCase)
                || enumName.Contains("Overhead", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static bool SetMemberIfExists(object target, string name, object value)
    {
        Type type = target.GetType();

        try
        {
            (PropertyInfo? prop, FieldInfo? field) = writeMemberCache.GetOrAdd((type, name), static key =>
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo? p = key.Item1.GetProperty(key.Item2, flags);
                return (p?.CanWrite == true ? p : null, key.Item1.GetField(key.Item2, flags));
            });

            if (prop != null)
            {
                object converted = ConvertValue(value, prop.PropertyType);
                prop.SetValue(target, converted);
                return true;
            }

            if (field != null)
            {
                object converted = ConvertValue(value, field.FieldType);
                field.SetValue(target, converted);
                return true;
            }
        }
        catch
        {
            // Reflection is only used for version-shifting internals such as camera transforms.
            // Never allow a bad reflective assignment to crash the client render loop.
            return false;
        }

        return false;
    }

    private static object ConvertValue(object value, Type targetType)
    {
        if (value == null) return value!;
        if (targetType.IsInstanceOfType(value)) return value;
        if (targetType == typeof(float) && value is double d) return (float)d;
        if (targetType == typeof(double) && value is float f) return (double)f;

        if (value is Vec3f vec && LooksLikeVector3(targetType))
        {
            object? converted = CreateVector3(targetType, vec.X, vec.Y, vec.Z);
            if (converted != null) return converted;
        }

        return value;
    }

    private static bool LooksLikeVector3(Type type)
    {
        string name = type.FullName ?? type.Name;
        return name.Contains("Vec3", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FastVec3", StringComparison.OrdinalIgnoreCase);
    }

    private static object? CreateVector3(Type targetType, float x, float y, float z)
    {
        try
        {
            ConstructorInfo? ctor = targetType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
            if (ctor != null) return ctor.Invoke(new object[] { x, y, z });

            object? instance = Activator.CreateInstance(targetType);
            if (instance == null) return null;

            SetVectorMember(instance, "X", x);
            SetVectorMember(instance, "Y", y);
            SetVectorMember(instance, "Z", z);
            SetVectorMember(instance, "x", x);
            SetVectorMember(instance, "y", y);
            SetVectorMember(instance, "z", z);

            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static void SetVectorMember(object target, string name, float value)
    {
        Type type = target.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? prop = type.GetProperty(name, flags);
        if (prop?.CanWrite == true)
        {
            prop.SetValue(target, value);
            return;
        }

        FieldInfo? field = type.GetField(name, flags);
        if (field != null) field.SetValue(target, value);
    }
}
public static class NewfiesAfkHungerReductionCompatibilityPatch
{
    public static bool Prefix(object __instance, ref bool __result)
    {
        NewfiesAfkModSystem? instance = NewfiesAfkModSystem.Instance;
        if (instance == null || !instance.ShouldPauseHungerForAfk(__instance)) return true;

        __result = false;
        return false;
    }
}

public static class NewfiesAfkHungerSlowTickCompatibilityPatch
{
    public static bool Prefix(object __instance)
    {
        NewfiesAfkModSystem? instance = NewfiesAfkModSystem.Instance;
        if (instance == null) return true;

        return !instance.ShouldPauseHungerForAfk(__instance);
    }
}

public static class NewfiesAfkHydrateOrDiedrateCompatibilityPatch
{
    public static bool Prefix(object __instance)
    {
        NewfiesAfkModSystem? instance = NewfiesAfkModSystem.Instance;
        if (instance == null) return true;

        return !instance.ShouldPauseHydrateOrDiedrateThirstForAfk(__instance);
    }
}

public static class NewfiesAfkXSkillsCompatibilityPatch
{
    public static bool Prefix(object __instance, float __0)
    {
        NewfiesAfkModSystem? instance = NewfiesAfkModSystem.Instance;
        if (instance == null) return true;

        return !instance.ShouldSuppressXSkillsSurvivalExperience(__instance, __0);
    }
}

public static class NewfiesAfkDamagePatch
{
    public static MethodBase? TargetMethod()
    {
        // Keep this tolerant of minor API signature changes. Vintage Story 1.22.7 exposes
        // ReceiveDamage(DamageSource, float), returning true when damage is actually applied.
        return typeof(Entity).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == nameof(Entity.ReceiveDamage) && method.GetParameters().Length >= 2);
    }

    public static void Postfix(Entity __instance, object[] __args, bool __result)
    {
        // Lightweight combat tagging: this runs only when an entity is actually damaged,
        // not on a repeating scan of every online player.
        if (!__result) return;
        if (__instance is not EntityPlayer playerEntity) return;

        float damage = 1f;
        if (__args.Length >= 2 && __args[1] is float f) damage = f;

        NewfiesAfkModSystem.Instance?.MarkDamaged(playerEntity, damage);
    }
}
