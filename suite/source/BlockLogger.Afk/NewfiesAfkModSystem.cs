using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BlockLogger.Config;
using BlockLogger.Services;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Afk;

public class NewfiesAfkModSystem : ModSystem, IRenderer, IDisposable
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

	private AfkConfig config = new AfkConfig();

	private Harmony? harmony;

	private bool xSkillsCompatibilityPatched;

	private bool hungerCompatibilityPatched;

	private bool hydrateOrDiedrateCompatibilityPatched;

	private readonly Dictionary<string, AfkPlayerState> afkPlayers = new Dictionary<string, AfkPlayerState>();

	private readonly HashSet<string> cameraCapablePlayers = new HashSet<string>(StringComparer.Ordinal);

	private readonly Dictionary<string, long> lastDamageUnixMsByPlayerUid = new Dictionary<string, long>();

	private readonly Dictionary<string, long> lastCorrectionUnixMsByPlayerUid = new Dictionary<string, long>();

	private bool clientAfkCameraEnabled;

	private float previousCameraPitch;

	private float previousCameraYaw;

	private double orbitAngleRadians;

	private AfkCameraPacket currentCameraPacket = new AfkCameraPacket();

	private object? previousCameraMode;

	private bool previousCameraModeWasCaptured;

	private long lastThirdPersonForceMs;

	private long clientCameraTickId;

	private float lastAppliedOrbitYaw;

	private double orbitBaseYawRadians;

	private long clientCameraStartMs;

	private bool renderFrameCameraRegistered;

	private long serverLockTickId = -1L;

	private long clientHelloListenerId = -1L;

	private bool clientHelloSent;

	private int clientHelloAttempts;

	private bool clientLevelFinalizeHooked;

	private bool serverEventsHooked;

	private bool serverStarted;

	private bool clientStarted;

	internal static NewfiesAfkModSystem? Instance { get; private set; }

	public static NewfiesAfkModSystem? Current => Instance;

	public bool RootAfkCommandRegistered { get; private set; }

	public double RenderOrder => 0.5;

	public int RenderRange => 999999;

	public override bool ShouldLoad(EnumAppSide side)
	{
		return true;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;
		if (api.ModLoader.Mods.Any((Mod mod) => mod?.Info != null && string.Equals(mod.Info.ModID, "newfiesafk", StringComparison.OrdinalIgnoreCase)))
		{
			api.Logger.Error("[ServerEssentials/AFK] Standalone NewfiesAfk is also installed. Remove the standalone mod; the integrated AFK subsystem has been disabled for this run to avoid duplicate commands/patches.");
			return;
		}
		SuiteConfigStorage.InitializeAndRelocateLegacyFiles(api);
		SuiteFeatureConfig suiteFeatureConfig = SuiteFeatureConfig.Load(api);
		if (!suiteFeatureConfig.Afk)
		{
			api.Logger.Notification("[ServerEssentials/AFK] Entire subsystem disabled by {0}; no AFK command, lock tick, survival patches, or camera channel registered.", "NewfiesServerEssentialsFeatures.json");
			return;
		}
		Instance = this;
		serverStarted = true;
		config = LoadOrCreateConfig(api);
		api.Permissions.RegisterPrivilege("afk", "Ability to use the /afk command.");
		harmony = new Harmony("newfiesserveressentialssuite.afk");
		TryInstallDamageTrackingPatch();
		TryInstallXSkillsCompatibility();
		TryInstallHungerCompatibility();
		TryInstallHydrateOrDiedrateCompatibility();
		serverChannel = api.Network.GetChannel("newfies-suite-afk-v1") ?? api.Network.RegisterChannel("newfies-suite-afk-v1");
		serverChannel.RegisterMessageType(typeof(AfkCameraPacket)).RegisterMessageType(typeof(AfkClientHelloPacket)).SetMessageHandler<AfkClientHelloPacket>(OnAfkClientHello);
		if (api.ChatCommands.Get("afk") == null)
		{
			api.ChatCommands.Create("afk").WithDescription("Toggle locked spectator AFK mode.").RequiresPrivilege("afk")
				.RequiresPlayer()
				.HandleWith(OnAfkCommand);
			RootAfkCommandRegistered = true;
		}
		else
		{
			api.Logger.Warning("[ServerEssentials/AFK] Root /afk is already owned by another provider. Use /nse afk as the Suite fallback.");
		}
		if (api.ChatCommands.Get("afkstatus") == null)
		{
			api.ChatCommands.Create("afkstatus").WithDescription("Show Server Essentials AFK debug status.").RequiresPrivilege(Privilege.chat)
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
			try
			{
				sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
			}
			catch
			{
			}
			try
			{
				sapi.Event.PlayerDeath -= OnPlayerDeath;
			}
			catch
			{
			}
			serverEventsHooked = false;
		}
		if (serverLockTickId >= 0 && sapi != null)
		{
			try
			{
				sapi.Event.UnregisterGameTickListener(serverLockTickId);
			}
			catch
			{
			}
			serverLockTickId = -1L;
		}
		if (clientLevelFinalizeHooked && capi != null)
		{
			try
			{
				capi.Event.LevelFinalize -= OnClientLevelFinalize;
			}
			catch
			{
			}
			clientLevelFinalizeHooked = false;
		}
		StopClientHelloLoop();
		if (clientStarted)
		{
			try
			{
				StopClientAfkCamera(restoreViewAngles: true, showMessage: false);
			}
			catch
			{
			}
			clientStarted = false;
		}
		try
		{
			harmony?.UnpatchAll("newfiesserveressentialssuite.afk");
		}
		catch
		{
		}
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
		if (api.ModLoader.Mods.Any((Mod mod) => mod?.Info != null && string.Equals(mod.Info.ModID, "newfiesafk", StringComparison.OrdinalIgnoreCase)))
		{
			api.Logger.Warning("[ServerEssentials/AFK] Standalone NewfiesAfk is installed on this client. Integrated AFK camera support is disabled to avoid duplicate camera control.");
			return;
		}
		clientStarted = true;
		config = LoadOrCreateConfig(api);
		api.Logger.Notification("[ServerEssentials/AFK] Client camera support loaded. UseCameraOffset={0}, UseVanillaThirdPersonZoom={1}, CameraDistance={2}", config.UseCameraOffset, config.UseVanillaThirdPersonZoom, config.CameraDistance);
		clientChannel = api.Network.GetChannel("newfies-suite-afk-v1") ?? api.Network.RegisterChannel("newfies-suite-afk-v1");
		clientChannel.RegisterMessageType(typeof(AfkCameraPacket)).RegisterMessageType(typeof(AfkClientHelloPacket)).SetMessageHandler<AfkCameraPacket>(OnAfkCameraPacket);
		api.Event.LevelFinalize += OnClientLevelFinalize;
		clientLevelFinalizeHooked = true;
		StartClientHelloLoop();
		if (api.PlayerReadyFired)
		{
			TrySendClientHello();
		}
	}

	private AfkConfig LoadOrCreateConfig(ICoreAPI api)
	{
		AfkConfig afkConfig = null;
		try
		{
			if (api is ICoreServerAPI api2)
			{
				afkConfig = ModConfigRecovery.LoadOrNull<AfkConfig>(api2, "NewfiesAfk.json", "ServerEssentials/AFK");
			}
			else
			{
				try
				{
					Directory.CreateDirectory(Path.Combine(api.DataBasePath, "ModConfig", "NewfiesServerEssentialsSuite"));
				}
				catch
				{
				}
				afkConfig = api.LoadModConfig<AfkConfig>(SuiteConfigStorage.RelativePath("NewfiesAfk.json"));
				if (afkConfig == null)
				{
					afkConfig = api.LoadModConfig<AfkConfig>("NewfiesAfk.json");
					if (afkConfig != null)
					{
						api.Logger.Notification("[ServerEssentials/AFK] Importing legacy client AFK config into ModConfig/{0}/.", "NewfiesServerEssentialsSuite");
					}
				}
			}
		}
		catch (ConfigMigrationException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			api.Logger.Warning("[ServerEssentials/AFK] Could not load config, using defaults. Error: {0}", ex2.Message);
		}
		if (afkConfig == null)
		{
			afkConfig = new AfkConfig();
		}
		afkConfig.Clamp();
		if (api is ICoreServerAPI api3)
		{
			SuiteConfigStorage.Store(api3, afkConfig, "NewfiesAfk.json");
		}
		else
		{
			api.StoreModConfig(afkConfig, SuiteConfigStorage.RelativePath("NewfiesAfk.json"));
		}
		return afkConfig;
	}

	private void OnAfkClientHello(IServerPlayer player, AfkClientHelloPacket packet)
	{
		if (player != null && packet != null && packet.ProtocolVersion == 1)
		{
			cameraCapablePlayers.Add(player.PlayerUID);
			if (config.DebugLogging)
			{
				sapi?.Logger.Debug("[ServerEssentials/AFK] Client camera handshake accepted for {0}; clientVersion={1}.", player.PlayerName, packet.ClientVersion ?? "");
			}
			if (afkPlayers.ContainsKey(player.PlayerUID))
			{
				SendCameraPacketIfSupported(player, enabled: true);
			}
		}
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
		if (capi == null || clientHelloSent || clientHelloListenerId >= 0 || TrySendClientHello())
		{
			return;
		}
		clientHelloListenerId = capi.Event.RegisterGameTickListener((float _) =>
		{
			if (clientHelloSent || clientHelloAttempts >= 30)
			{
				StopClientHelloLoop();
			}
			else
			{
				clientHelloAttempts++;
				TrySendClientHello();
			}
		}, 1000);
	}

	private void StopClientHelloLoop()
	{
		if (capi != null && clientHelloListenerId >= 0)
		{
			try
			{
				capi.Event.UnregisterGameTickListener(clientHelloListenerId);
			}
			catch
			{
			}
			clientHelloListenerId = -1L;
		}
	}

	private bool TrySendClientHello()
	{
		if (capi == null || clientChannel == null || clientHelloSent)
		{
			return clientHelloSent;
		}
		if (capi.Network.GetChannelState("newfies-suite-afk-v1") != EnumChannelState.Connected)
		{
			return false;
		}
		try
		{
			clientChannel.SendPacket(new AfkClientHelloPacket
			{
				ProtocolVersion = 1,
				ClientVersion = "1.2.44"
			});
			clientHelloSent = true;
			StopClientHelloLoop();
			return true;
		}
		catch (Exception ex)
		{
			if (config.DebugLogging)
			{
				capi.Logger.Debug("[ServerEssentials/AFK] Camera hello not ready: {0}: {1}", ex.GetType().Name, ex.Message);
			}
			return false;
		}
	}

	private void SendCameraPacketIfSupported(IServerPlayer player, bool enabled)
	{
		if (serverChannel == null || player == null)
		{
			return;
		}
		if (!cameraCapablePlayers.Contains(player.PlayerUID))
		{
			if (config.DebugLogging)
			{
				sapi?.Logger.Debug("[ServerEssentials/AFK] {0} has no integrated Suite AFK client handshake; server AFK state will run without the orbit camera.", player.PlayerName);
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
			sapi?.Logger.Debug("[ServerEssentials/AFK] Camera packet failed for {0}; client capability was cleared: {1}: {2}", player.PlayerName, ex.GetType().Name, ex.Message);
		}
	}

	public bool IsAfk(string? playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && afkPlayers.ContainsKey(playerUid);
	}

	public (bool Success, string Message) ToggleForPlayer(IServerPlayer player)
	{
		if (sapi == null || serverChannel == null || !serverStarted)
		{
			return (Success: false, Message: "AFK system is not ready.");
		}
		if (player?.Entity == null)
		{
			return (Success: false, Message: "Only an online player can use /afk.");
		}
		if (afkPlayers.ContainsKey(player.PlayerUID))
		{
			DisableAfk(player, restorePosition: false);
			return (Success: true, Message: "AFK mode disabled.");
		}
		if (IsCombatCooldownActive(player, out var secondsRemaining))
		{
			string text = FormatCombatBlockMessage(secondsRemaining);
			player.SendMessage(GlobalConstants.GeneralChatGroup, text, EnumChatType.Notification);
			return (Success: false, Message: text);
		}
		if (config.RequireGroundedToEnable && !IsStandingOnGroundForAfk(player.Entity, out string error))
		{
			player.SendMessage(GlobalConstants.GeneralChatGroup, error, EnumChatType.Notification);
			return (Success: false, Message: error);
		}
		if (!EnableAfk(player, out string error2))
		{
			return (Success: false, Message: error2);
		}
		return (Success: true, Message: "AFK mode enabled.");
	}

	private TextCommandResult OnAfkCommand(TextCommandCallingArgs args)
	{
		if (!(args.Caller.Player is IServerPlayer player))
		{
			return TextCommandResult.Error("Only an online player can use /afk.");
		}
		(bool, string) tuple = ToggleForPlayer(player);
		return tuple.Item1 ? TextCommandResult.Success(tuple.Item2) : TextCommandResult.Error(tuple.Item2);
	}

	private TextCommandResult OnAfkStatusCommand(TextCommandCallingArgs args)
	{
		if (!(args.Caller.Player is IServerPlayer serverPlayer))
		{
			return TextCommandResult.Error("Only an online player can use this command.");
		}
		bool value = afkPlayers.ContainsKey(serverPlayer.PlayerUID);
		bool value2 = IsCombatCooldownActive(serverPlayer, out var secondsRemaining);
		return TextCommandResult.Success($"ServerEssentials AFK: loaded=true, youAfk={value}, afkPlayers={afkPlayers.Count}, combatCooldownActive={value2}, secondsRemaining={secondsRemaining}, xskillsXpBlock={config.DisableXSkillsSurvivalXpWhileAfk}, xskillsPatched={xSkillsCompatibilityPatched}, stopMobSpawns={config.StopMobSpawnsAroundAfkPlayers}, pauseHunger={config.PauseHungerWhileAfk}, hungerPatched={hungerCompatibilityPatched}, pauseHoDThirst={config.PauseHydrateOrDiedrateThirstWhileAfk}, hodPatched={hydrateOrDiedrateCompatibilityPatched}, autoKick={config.AutoKickAfkEnabled}, autoKickMinutes={config.AutoKickAfterMinutes:0.##}, clientCamera={config.EnableClientCamera}, serverEntitySpin={config.EnableServerEntitySpin}, spinDegPerSec={config.CameraOrbitSpeedDegreesPerSecond:0.##}, debug={config.DebugLogging}");
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
		lastCorrectionUnixMsByPlayerUid[player.PlayerUID] = 0L;
		if (config.StopMobSpawnsAroundAfkPlayers)
		{
			EntityPlayer entity = player.Entity;
			entity.OnCanSpawnNearby = (CanSpawnNearbyDelegate)Delegate.Remove(entity.OnCanSpawnNearby, new CanSpawnNearbyDelegate(DenyCreatureSpawnNearAfkPlayer));
			EntityPlayer entity2 = player.Entity;
			entity2.OnCanSpawnNearby = (CanSpawnNearbyDelegate)Delegate.Combine(entity2.OnCanSpawnNearby, new CanSpawnNearbyDelegate(DenyCreatureSpawnNearAfkPlayer));
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
			sapi.BroadcastMessageToAllGroups(player.PlayerName + " is now AFK.", EnumChatType.Notification);
		}
		return true;
	}

	private void DisableAfk(IServerPlayer player, bool restorePosition)
	{
		if (sapi != null && serverChannel != null && afkPlayers.TryGetValue(player.PlayerUID, out AfkPlayerState value))
		{
			if (player.Entity != null)
			{
				EntityPlayer entity = player.Entity;
				entity.OnCanSpawnNearby = (CanSpawnNearbyDelegate)Delegate.Remove(entity.OnCanSpawnNearby, new CanSpawnNearbyDelegate(DenyCreatureSpawnNearAfkPlayer));
			}
			afkPlayers.Remove(player.PlayerUID);
			lastCorrectionUnixMsByPlayerUid.Remove(player.PlayerUID);
			if (config.DebugLogging)
			{
				sapi.Logger.Notification("[ServerEssentials/AFK] Disabling AFK for {0}. restorePosition={1}", player.PlayerName, restorePosition);
			}
			if (restorePosition && player.Entity != null)
			{
				TeleportEntityToLockedPos(player.Entity, value);
				ZeroControlsAndMotion(player.Entity);
			}
			player.WorldData.CurrentGameMode = GetReturnGameMode(value);
			player.BroadcastPlayerData();
			SendCameraPacketIfSupported(player, enabled: false);
			player.SendMessage(GlobalConstants.GeneralChatGroup, "You are no longer AFK.", EnumChatType.Notification);
			if (config.BroadcastMessages)
			{
				sapi.BroadcastMessageToAllGroups(player.PlayerName + " is no longer AFK.", EnumChatType.Notification);
			}
		}
	}

	private EnumGameMode GetReturnGameMode(AfkPlayerState state)
	{
		if (config.RestoreOriginalGameMode)
		{
			return state.OriginalGameMode;
		}
		if (!string.IsNullOrWhiteSpace(config.ReturnGameMode) && Enum.TryParse<EnumGameMode>(config.ReturnGameMode, ignoreCase: true, out var result))
		{
			return result;
		}
		return EnumGameMode.Survival;
	}

	private bool IsStandingOnGroundForAfk(EntityPlayer entity, out string error)
	{
		error = (string.IsNullOrWhiteSpace(config.NotGroundedBlockMessage) ? "You must be standing on solid ground to use /afk." : config.NotGroundedBlockMessage);
		if (GetBoolMember(entity, "Swimming") || GetBoolMember(entity, "FeetInLiquid"))
		{
			error = "You must be standing on solid ground, not swimming or standing in liquid, to use /afk.";
			return false;
		}
		if (TryGetVerticalMotion(entity, out var motionY) && Math.Abs(motionY) > Math.Max(0.001, config.MaxVerticalMotionForGroundedAfk))
		{
			return false;
		}
		if (GetBoolMember(entity, "OnGround") || GetBoolMember(entity, "CollidedVertically"))
		{
			return true;
		}
		return false;
	}

	private static bool TryGetVerticalMotion(Entity entity, out double motionY)
	{
		motionY = 0.0;
		object entityPositionObject = GetEntityPositionObject(entity);
		object obj = ((entityPositionObject == null) ? null : GetMemberValue(entityPositionObject, "Motion"));
		if (obj == null)
		{
			return false;
		}
		object obj2 = GetMemberValue(obj, "Y") ?? GetMemberValue(obj, "y");
		object obj3 = obj2;
		object obj4 = obj3;
		if (!(obj4 is double num))
		{
			if (!(obj4 is float num2))
			{
				if (obj4 is int num3)
				{
					motionY = num3;
					return true;
				}
				return false;
			}
			motionY = num2;
			return true;
		}
		motionY = num;
		return true;
	}

	private static bool GetBoolMember(object target, string name)
	{
		object memberValue = GetMemberValue(target, name);
		bool flag = default;
		int num;
		if (memberValue is bool)
		{
			flag = (bool)memberValue;
			num = 1;
		}
		else
		{
			num = 0;
		}
		return (byte)((uint)num & (flag ? 1u : 0u)) != 0;
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
		return false;
	}

	private void TryInstallDamageTrackingPatch()
	{
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = NewfiesAfkDamagePatch.TargetMethod();
			MethodInfo methodInfo = AccessTools.Method(typeof(NewfiesAfkDamagePatch), "Postfix");
			if (methodBase == null || methodInfo == null)
			{
				sapi?.Logger.Warning("[ServerEssentials/AFK] Player damage tracking patch target was not found; combat AFK cooldown may be unavailable.");
			}
			else
			{
				harmony.Patch(methodBase, null, new HarmonyMethod(methodInfo));
			}
		}
		catch (Exception ex)
		{
			sapi?.Logger.Warning("[ServerEssentials/AFK] Damage tracking patch failed safely: {0}: {1}", ex.GetType().Name, ex.Message);
		}
	}

	private void TryInstallHungerCompatibility()
	{
		if (harmony == null || !config.PauseHungerWhileAfk)
		{
			return;
		}
		try
		{
			Type type = AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorHunger");
			if (type == null)
			{
				sapi?.Logger.Warning("[ServerEssentials/AFK] Vanilla hunger behavior was not found. AFK hunger protection is unavailable.");
				return;
			}
			MethodInfo methodInfo = AccessTools.Method(type, "ReduceSaturation", new Type[1] { typeof(float) });
			MethodInfo methodInfo2 = AccessTools.Method(typeof(NewfiesAfkHungerReductionCompatibilityPatch), "Prefix");
			MethodInfo methodInfo3 = AccessTools.Method(type, "SlowTick", new Type[1] { typeof(float) });
			MethodInfo methodInfo4 = AccessTools.Method(typeof(NewfiesAfkHungerSlowTickCompatibilityPatch), "Prefix");
			int num = 0;
			if (methodInfo != null && methodInfo2 != null)
			{
				harmony.Patch(methodInfo, new HarmonyMethod(methodInfo2));
				num++;
			}
			if (methodInfo3 != null && methodInfo4 != null)
			{
				harmony.Patch(methodInfo3, new HarmonyMethod(methodInfo4));
				num++;
			}
			hungerCompatibilityPatched = num > 0;
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
		if (harmony == null || !config.PauseHydrateOrDiedrateThirstWhileAfk)
		{
			return;
		}
		try
		{
			Type type = AccessTools.TypeByName("HydrateOrDiedrate.EntityBehaviorThirst");
			if (type == null)
			{
				if (config.DebugLogging)
				{
					sapi?.Logger.Notification("[ServerEssentials/AFK] Hydrate or Diedrate not detected; AFK thirst protection was not patched.");
				}
				return;
			}
			MethodInfo methodInfo = AccessTools.Method(type, "OnGameTick", new Type[1] { typeof(float) });
			MethodInfo methodInfo2 = AccessTools.Method(typeof(NewfiesAfkHydrateOrDiedrateCompatibilityPatch), "Prefix");
			if (methodInfo == null || methodInfo2 == null)
			{
				sapi?.Logger.Warning("[ServerEssentials/AFK] Hydrate or Diedrate detected, but EntityBehaviorThirst.OnGameTick(float) could not be patched.");
				return;
			}
			harmony.Patch(methodInfo, new HarmonyMethod(methodInfo2));
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
			object obj = GetMemberValue(behavior, "entity") ?? GetMemberValue(behavior, "Entity");
			if (!(obj is EntityPlayer { Player: var player } entityPlayer))
			{
				return false;
			}
			string text = player?.PlayerUID;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = GetMemberValue(entityPlayer, "PlayerUID") as string;
			}
			return !string.IsNullOrWhiteSpace(text) && afkPlayers.ContainsKey(text);
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
		if (harmony == null || !config.DisableXSkillsSurvivalXpWhileAfk)
		{
			return;
		}
		try
		{
			Type type = AccessTools.TypeByName("XLib.XLeveling.PlayerSkill");
			if (type == null)
			{
				if (config.DebugLogging)
				{
					sapi?.Logger.Notification("[ServerEssentials/AFK] XSkills/XLib not detected; Survival XP AFK protection not patched.");
				}
				return;
			}
			MethodInfo methodInfo = AccessTools.Method(type, "AddExperience", new Type[2]
			{
				typeof(float),
				typeof(bool)
			});
			if (methodInfo == null)
			{
				sapi?.Logger.Warning("[ServerEssentials/AFK] XSkills/XLib detected, but PlayerSkill.AddExperience(float,bool) was not found. Survival XP AFK protection is unavailable.");
				return;
			}
			MethodInfo methodInfo2 = AccessTools.Method(typeof(NewfiesAfkXSkillsCompatibilityPatch), "Prefix");
			if (methodInfo2 == null)
			{
				sapi?.Logger.Warning("[ServerEssentials/AFK] Could not locate the internal XSkills compatibility prefix.");
				return;
			}
			harmony.Patch(methodInfo, new HarmonyMethod(methodInfo2));
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
		if (!config.DisableXSkillsSurvivalXpWhileAfk || experience <= 0f)
		{
			return false;
		}
		try
		{
			object memberValue = GetMemberValue(playerSkill, "Skill");
			if (memberValue == null)
			{
				return false;
			}
			string a = GetMemberValue(memberValue, "Name") as string;
			if (!string.Equals(a, "survival", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			object memberValue2 = GetMemberValue(playerSkill, "PlayerSkillSet");
			object obj = ((memberValue2 == null) ? null : GetMemberValue(memberValue2, "Player"));
			string text;
			if (obj is IPlayer player)
			{
				text = player.PlayerUID;
			}
			else
			{
				text = ((obj == null) ? null : (GetMemberValue(obj, "PlayerUID") as string));
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			bool flag = afkPlayers.ContainsKey(text);
			if (flag && config.DebugLogging)
			{
				sapi?.Logger.Notification("[ServerEssentials/AFK] Suppressed {0:0.###} XSkills Survival XP for AFK uid={1}.", experience, text);
			}
			return flag;
		}
		catch (Exception ex)
		{
			if (config.DebugLogging)
			{
				sapi?.Logger.Warning("[ServerEssentials/AFK] XSkills AFK XP check failed open: {0}", ex.Message);
			}
			return false;
		}
	}

	internal void MarkDamaged(EntityPlayer entity, float damage)
	{
		if (sapi == null || damage <= 0f)
		{
			return;
		}
		string playerUID = entity.PlayerUID;
		if (!string.IsNullOrWhiteSpace(playerUID))
		{
			lastDamageUnixMsByPlayerUid[playerUID] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (config.DebugLogging)
			{
				sapi.Logger.Notification("[ServerEssentials/AFK] Damage recorded for uid={0}. damage={1}", playerUID, damage);
			}
			if (config.DisableAfkWhenDamaged && entity.Player is IServerPlayer serverPlayer && afkPlayers.ContainsKey(playerUID))
			{
				DisableAfk(serverPlayer, restorePosition: false);
				serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, "AFK mode disabled because you took damage.", EnumChatType.Notification);
			}
		}
	}

	private bool IsCombatCooldownActive(IServerPlayer player, out int secondsRemaining)
	{
		secondsRemaining = 0;
		if (config.CombatCooldownSeconds <= 0.0)
		{
			return false;
		}
		if (!lastDamageUnixMsByPlayerUid.TryGetValue(player.PlayerUID, out var value))
		{
			return false;
		}
		double num = (double)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - value) / 1000.0;
		double num2 = config.CombatCooldownSeconds - num;
		if (num2 <= 0.0)
		{
			return false;
		}
		secondsRemaining = Math.Max(1, (int)Math.Ceiling(num2));
		return true;
	}

	private string FormatCombatBlockMessage(int secondsRemaining)
	{
		string text = (string.IsNullOrWhiteSpace(config.CombatBlockMessage) ? "You cannot go AFK for {seconds} more seconds because you were recently damaged." : config.CombatBlockMessage);
		return text.Replace("{seconds}", secondsRemaining.ToString()).Replace("{minutes}", Math.Ceiling((double)secondsRemaining / 60.0).ToString("0"));
	}

	private static double? TryGetCurrentHealth(EntityPlayer entity)
	{
		try
		{
			EntityBehavior behavior = entity.GetBehavior("health");
			if (behavior == null)
			{
				return null;
			}
			object obj = GetMemberValue(behavior, "Health") ?? GetMemberValue(behavior, "health") ?? GetMemberValue(behavior, "CurrentHealth") ?? GetMemberValue(behavior, "currentHealth");
			if (obj is float num)
			{
				return num;
			}
			if (obj is double value)
			{
				return value;
			}
			if (obj is int num2)
			{
				return num2;
			}
			if (obj is long num3)
			{
				return num3;
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private static object? GetMemberValue(object target, string name)
	{
		Type type = target.GetType();
		while (type != null)
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
			{
				return property.GetValue(target);
			}
			FieldInfo field = type.GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				return field.GetValue(target);
			}
			type = type.BaseType;
		}
		return null;
	}

	private bool TryHandleAutoKick(IServerPlayer player, AfkPlayerState state)
	{
		if (!config.AutoKickAfkEnabled || config.AutoKickAfterMinutes <= 0.0)
		{
			return false;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (state.EnteredAfkUnixMs <= 0)
		{
			state.EnteredAfkUnixMs = num;
			return false;
		}
		double num2 = config.AutoKickAfterMinutes * 60000.0;
		double num3 = Math.Max(0L, num - state.EnteredAfkUnixMs);
		double num4 = (num2 - num3) / 1000.0;
		if (num4 <= 0.0)
		{
			string message = (string.IsNullOrWhiteSpace(config.AutoKickReason) ? "Kicked for being AFK too long." : config.AutoKickReason);
			if (config.DebugLogging)
			{
				sapi?.Logger.Notification("[ServerEssentials/AFK] Auto-kicking {0} after {1:0.##} minutes AFK.", player.PlayerName, config.AutoKickAfterMinutes);
			}
			RestoreAfkStateForDisconnect(player);
			player.Disconnect(message);
			return true;
		}
		int num5 = Math.Max(0, config.AutoKickWarningSeconds);
		if (num5 > 0 && !state.AutoKickWarningSent && num4 <= (double)num5)
		{
			state.AutoKickWarningSent = true;
			int secondsRemaining = Math.Max(1, (int)Math.Ceiling(num4));
			string message2 = FormatAutoKickWarning(secondsRemaining);
			player.SendMessage(GlobalConstants.GeneralChatGroup, message2, EnumChatType.Notification);
		}
		return false;
	}

	private string FormatAutoKickWarning(int secondsRemaining)
	{
		string text = (string.IsNullOrWhiteSpace(config.AutoKickWarningMessage) ? "You will be kicked in {seconds} seconds for being AFK too long." : config.AutoKickWarningMessage);
		return text.Replace("{seconds}", secondsRemaining.ToString()).Replace("{minutes}", Math.Ceiling((double)secondsRemaining / 60.0).ToString("0"));
	}

	private void OnServerLockTick(float dt)
	{
		if (sapi == null)
		{
			return;
		}
		try
		{
			IPlayer[] allOnlinePlayers = sapi.World.AllOnlinePlayers;
			for (int i = 0; i < allOnlinePlayers.Length; i++)
			{
				IServerPlayer serverPlayer = (IServerPlayer)allOnlinePlayers[i];
				if (serverPlayer?.Entity == null || !afkPlayers.TryGetValue(serverPlayer.PlayerUID, out AfkPlayerState value) || TryHandleAutoKick(serverPlayer, value))
				{
					continue;
				}
				if (config.ZeroControlsEveryLockTick)
				{
					ZeroControlsAndMotion(serverPlayer.Entity);
				}
				if (!TryReadEntityPosition(serverPlayer.Entity, out AfkPlayerState state))
				{
					continue;
				}
				double num = state.X - value.X;
				double num2 = state.Y - value.Y;
				double num3 = state.Z - value.Z;
				double num4 = num * num + num2 * num2 + num3 * num3;
				double num5 = Math.Max(0.01, config.LockDistanceTolerance);
				if (num4 > num5 * num5)
				{
					long num6 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
					lastCorrectionUnixMsByPlayerUid.TryGetValue(serverPlayer.PlayerUID, out var value2);
					if (num6 - value2 >= Math.Max(50, config.DriftCorrectionIntervalMs))
					{
						lastCorrectionUnixMsByPlayerUid[serverPlayer.PlayerUID] = num6;
						ZeroControlsAndMotion(serverPlayer.Entity);
						TeleportEntityToLockedPos(serverPlayer.Entity, value);
					}
				}
				else if (config.RewritePositionWhenWithinTolerance)
				{
					TryWriteEntityPosition(serverPlayer.Entity, value);
				}
				ApplyServerAfkSpin(serverPlayer.Entity, value);
			}
		}
		catch (Exception ex)
		{
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
		if (config.EnableServerEntitySpin && entity != null && state.EnteredAfkUnixMs > 0)
		{
			double num = (double)Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.EnteredAfkUnixMs) / 1000.0;
			double num2 = (double)state.Yaw + num * config.CameraOrbitSpeedDegreesPerSecond * (Math.PI / 180.0);
			num2 %= Math.PI * 2.0;
			if (num2 < 0.0)
			{
				num2 += Math.PI * 2.0;
			}
			float num3 = (float)num2;
			WriteViewYawToPosObject(GetMemberValue(entity, "Pos"), num3);
			WriteViewYawToPosObject(GetMemberValue(entity, "SidedPos"), num3);
			WriteViewYawToPosObject(GetMemberValue(entity, "ServerPos"), num3);
			SetMemberIfExists(entity, "Yaw", num3);
			SetMemberIfExists(entity, "BodyYaw", num3);
		}
	}

	private static bool TryReadEntityPosition(Entity entity, out AfkPlayerState state)
	{
		state = new AfkPlayerState();
		object entityPositionObject = GetEntityPositionObject(entity);
		if (entityPositionObject == null)
		{
			return false;
		}
		state.X = GetDoubleMember(entityPositionObject, "X");
		state.Y = GetDoubleMember(entityPositionObject, "Y");
		state.Z = GetDoubleMember(entityPositionObject, "Z");
		state.Yaw = GetFloatMember(entityPositionObject, "Yaw");
		state.Pitch = GetFloatMember(entityPositionObject, "Pitch");
		state.Roll = GetFloatMember(entityPositionObject, "Roll");
		return true;
	}

	private static bool TryWriteEntityPosition(Entity entity, AfkPlayerState state)
	{
		object entityPositionObject = GetEntityPositionObject(entity);
		if (entityPositionObject == null)
		{
			return false;
		}
		SetMemberIfExists(entityPositionObject, "X", state.X);
		SetMemberIfExists(entityPositionObject, "Y", state.Y);
		SetMemberIfExists(entityPositionObject, "Z", state.Z);
		SetMemberIfExists(entityPositionObject, "Yaw", state.Yaw);
		return true;
	}

	private static object? GetEntityPositionObject(Entity entity)
	{
		return GetMemberValue(entity, "Pos") ?? GetMemberValue(entity, "SidedPos") ?? GetMemberValue(entity, "ServerPos");
	}

	private static double GetDoubleMember(object target, string name)
	{
		object memberValue = GetMemberValue(target, name);
		if (1 == 0)
		{
		}
		double result;
		if (memberValue is double num)
		{
			result = num;
		}
		else if (memberValue is float num2)
		{
			result = num2;
		}
		else if (memberValue is int num3)
		{
			result = num3;
		}
		else
		{
			result = ((!(memberValue is long num4)) ? 0.0 : ((double)num4));
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static float GetFloatMember(object target, string name)
	{
		object memberValue = GetMemberValue(target, name);
		if (1 == 0)
		{
		}
		float result;
		if (memberValue is float num)
		{
			result = num;
		}
		else if (memberValue is double num2)
		{
			result = (float)num2;
		}
		else if (memberValue is int num3)
		{
			result = num3;
		}
		else
		{
			result = ((!(memberValue is long num4)) ? 0f : ((float)num4));
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static void ZeroControlsAndMotion(EntityPlayer entity)
	{
		object entityPositionObject = GetEntityPositionObject(entity);
		object obj = ((entityPositionObject == null) ? null : GetMemberValue(entityPositionObject, "Motion"));
		if (obj != null)
		{
			InvokeMethodIfExists(obj, "Set", 0.0, 0.0, 0.0);
			InvokeMethodIfExists(obj, "Set", 0f, 0f, 0f);
		}
		entity.Controls?.StopAllMovement();
		entity.ServerControls?.StopAllMovement();
		if (entity.Controls != null)
		{
			entity.Controls.FlyVector.Set(0.0, 0.0, 0.0);
			entity.Controls.WalkVector.Set(0.0, 0.0, 0.0);
		}
		if (entity.ServerControls != null)
		{
			entity.ServerControls.FlyVector.Set(0.0, 0.0, 0.0);
			entity.ServerControls.WalkVector.Set(0.0, 0.0, 0.0);
		}
	}

	private static bool InvokeMethodIfExists(object target, string name, params object[] args)
	{
		MethodInfo methodInfo = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo methodInfo2) => methodInfo2.Name == name && methodInfo2.GetParameters().Length == args.Length);
		if (methodInfo == null)
		{
			return false;
		}
		try
		{
			methodInfo.Invoke(target, args);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player == null)
		{
			return;
		}
		cameraCapablePlayers.Remove(player.PlayerUID);
		if (config.RestoreGameModeOnDisconnect)
		{
			RestoreAfkStateForDisconnect(player);
		}
		else
		{
			if (player.Entity != null)
			{
				EntityPlayer entity = player.Entity;
				entity.OnCanSpawnNearby = (CanSpawnNearbyDelegate)Delegate.Remove(entity.OnCanSpawnNearby, new CanSpawnNearbyDelegate(DenyCreatureSpawnNearAfkPlayer));
			}
			afkPlayers.Remove(player.PlayerUID);
		}
		lastDamageUnixMsByPlayerUid.Remove(player.PlayerUID);
		lastCorrectionUnixMsByPlayerUid.Remove(player.PlayerUID);
	}

	private void RestoreAfkStateForDisconnect(IServerPlayer player)
	{
		if (!afkPlayers.TryGetValue(player.PlayerUID, out AfkPlayerState value))
		{
			return;
		}
		if (player.Entity != null)
		{
			EntityPlayer entity = player.Entity;
			entity.OnCanSpawnNearby = (CanSpawnNearbyDelegate)Delegate.Remove(entity.OnCanSpawnNearby, new CanSpawnNearbyDelegate(DenyCreatureSpawnNearAfkPlayer));
		}
		afkPlayers.Remove(player.PlayerUID);
		try
		{
			player.WorldData.CurrentGameMode = GetReturnGameMode(value);
			player.BroadcastPlayerData();
			if (config.DebugLogging && sapi != null)
			{
				sapi.Logger.Notification("[ServerEssentials/AFK] Restored gamemode for {0} on disconnect to {1}.", player.PlayerName, player.WorldData.CurrentGameMode);
			}
		}
		catch (Exception ex)
		{
			sapi?.Logger.Warning("[ServerEssentials/AFK] Failed to restore gamemode for {0} on disconnect: {1}", player.PlayerName, ex.Message);
		}
	}

	private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
	{
		if (player != null)
		{
			if (player?.Entity != null)
			{
				MarkDamaged(player.Entity, 1f);
			}
			if (player != null)
			{
				DisableAfk(player, restorePosition: false);
			}
		}
	}

	private AfkCameraPacket ApplyLocalClientCameraConfig(AfkCameraPacket packet)
	{
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
		if (capi == null)
		{
			return;
		}
		packet = ApplyLocalClientCameraConfig(packet);
		currentCameraPacket = packet;
		if (!packet.EnableClientCamera)
		{
			StopClientAfkCamera(restoreViewAngles: false, showMessage: false);
		}
		else if (packet.Enabled)
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
			lastThirdPersonForceMs = 0L;
			if (packet.TryForceThirdPerson)
			{
				TryForceThirdPersonCamera();
				lastThirdPersonForceMs = capi.ElapsedMilliseconds;
			}
			RestoreCameraOffset();
			EnsureClientCameraUpdaterRegistered();
			capi.World.Player.ShowChatNotification("AFK camera enabled.");
		}
		else
		{
			StopClientAfkCamera(restoreViewAngles: true, showMessage: true);
		}
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		OnClientCameraTick(deltaTime);
	}

	private void OnClientCameraTick(float dt)
	{
		if (!clientAfkCameraEnabled || capi == null || capi.World?.Player == null)
		{
			return;
		}
		bool flag = IsCurrentlyThirdPersonCamera();
		if (currentCameraPacket.TryForceThirdPerson)
		{
			int num = Math.Max(100, currentCameraPacket.ThirdPersonForceIntervalMs);
			if (currentCameraPacket.ForceThirdPersonEveryTick || (currentCameraPacket.ReforceThirdPersonIfLost && !flag && capi.ElapsedMilliseconds - lastThirdPersonForceMs >= num))
			{
				TryForceThirdPersonCamera();
				lastThirdPersonForceMs = capi.ElapsedMilliseconds;
				flag = IsCurrentlyThirdPersonCamera();
			}
		}
		if (!currentCameraPacket.OnlyOrbitWhenThirdPersonIsActive | flag)
		{
			double num2 = currentCameraPacket.CameraOrbitSpeedDegreesPerSecond * 0.01745329238474369;
			double num3 = Math.Max(0.0, (double)(capi.ElapsedMilliseconds - clientCameraStartMs) / 1000.0);
			orbitAngleRadians = orbitBaseYawRadians + num2 * num3;
			float num4 = (float)orbitAngleRadians;
			float num5 = num4;
			if (currentCameraPacket.SmoothOrbitYaw)
			{
				double num6 = Math.Max(0.001, (double)dt / (1.0 / 60.0));
				float maxStep = (float)(Math.Max(0.1, currentCameraPacket.MaxYawStepDegreesPerTick) * num6 * 0.01745329238474369);
				num5 = (lastAppliedOrbitYaw = StepAngleRadians(lastAppliedOrbitYaw, num4, maxStep));
			}
			float cameraPitch = (float)(currentCameraPacket.CameraPitchDegrees * 0.01745329238474369);
			if (currentCameraPacket.OrbitByEntityRotation)
			{
				ApplyEntityViewRotation(num5);
			}
			if (currentCameraPacket.WriteCameraYawOnly || currentCameraPacket.WriteCameraYawPitch)
			{
				capi.World.Player.CameraYaw = num5;
			}
			if (currentCameraPacket.WriteCameraYawPitch)
			{
				capi.World.Player.CameraPitch = cameraPitch;
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
		double num;
		for (num = target - current; num > Math.PI; num -= Math.PI * 2.0)
		{
		}
		for (; num < -Math.PI; num += Math.PI * 2.0)
		{
		}
		if (Math.Abs(num) <= (double)maxStep)
		{
			return target;
		}
		return current + (float)Math.Sign(num) * maxStep;
	}

	private void EnsureClientCameraUpdaterRegistered()
	{
		if (capi != null)
		{
			if (currentCameraPacket.UseRenderFrameCamera && !renderFrameCameraRegistered)
			{
				capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "newfies-suite-afk-camera");
				renderFrameCameraRegistered = true;
			}
			else if (!currentCameraPacket.UseRenderFrameCamera && clientCameraTickId == 0)
			{
				int millisecondInterval = Math.Max(5, (currentCameraPacket.ClientCameraTickMs <= 0) ? 16 : currentCameraPacket.ClientCameraTickMs);
				clientCameraTickId = capi.Event.RegisterGameTickListener(OnClientCameraTick, millisecondInterval);
			}
		}
	}

	private void StopClientAfkCamera(bool restoreViewAngles, bool showMessage)
	{
		if (capi != null)
		{
			clientAfkCameraEnabled = false;
			if (clientCameraTickId != 0)
			{
				capi.Event.UnregisterGameTickListener(clientCameraTickId);
				clientCameraTickId = 0L;
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
	}

	private void ApplyEntityViewRotation(float yaw)
	{
		if (capi?.World?.Player?.Entity == null)
		{
			return;
		}
		try
		{
			object entity = capi.World.Player.Entity;
			object memberValue = GetMemberValue(entity, "SidedPos");
			object memberValue2 = GetMemberValue(entity, "Pos");
			object memberValue3 = GetMemberValue(entity, "ServerPos");
			bool flag = false;
			flag |= WriteViewYawToPosObject(memberValue, yaw);
			flag |= WriteViewYawToPosObject(memberValue2, yaw);
			flag |= WriteViewYawToPosObject(memberValue3, yaw);
			flag |= SetMemberIfExists(entity, "Yaw", yaw);
			flag |= SetMemberIfExists(entity, "BodyYaw", yaw);
			object memberValue4 = GetMemberValue(entity, "Controls");
			if (memberValue4 != null)
			{
				flag |= SetMemberIfExists(memberValue4, "Yaw", yaw);
			}
			if (!flag && currentCameraPacket.DebugClientCameraReflection)
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
		if (pos == null)
		{
			return false;
		}
		bool flag = false;
		flag |= SetMemberIfExists(pos, "Yaw", yaw);
		flag |= SetMemberIfExists(pos, "HeadYaw", yaw);
		return flag | SetMemberIfExists(pos, "BodyYaw", yaw);
	}

	private void TryApplyVanillaThirdPersonDistance(double distance)
	{
		if (capi != null)
		{
			float distance2 = (float)Math.Max(1.0, distance);
			bool flag = false;
			object mainCameraObject = GetMainCameraObject();
			flag |= TrySetCameraDistanceMembers(mainCameraObject, distance2);
			flag |= TrySetCameraDistanceMembers(capi.Render, distance2);
			flag |= TrySetCameraDistanceMembers(capi.World?.Player, distance2);
			flag |= TrySetCameraDistanceMembers(capi.World?.Player?.Entity, distance2);
			object settings = GetMemberValue(capi, "Settings") ?? GetMemberValue(capi, "settings");
			if (!(flag | TrySetDistanceSetting(settings, distance2)) && currentCameraPacket.DebugClientCameraReflection)
			{
				capi.Logger.Warning("[ServerEssentials/AFK] Could not find a vanilla third-person distance member. CameraDistance will not work unless UseCameraOffset is enabled, but CameraOffset may clip into terrain.");
				LogLikelyCameraMembers(mainCameraObject, "MainCamera");
				LogLikelyCameraMembers(capi.Render, "Render");
				LogLikelyCameraMembers(capi.World?.Player, "Player");
			}
		}
	}

	private static bool TrySetCameraDistanceMembers(object? target, float distance)
	{
		if (target == null)
		{
			return false;
		}
		bool flag = false;
		string[] array = new string[20]
		{
			"ThirdPersonCameraDistance", "thirdPersonCameraDistance", "ThirdPersonDistance", "thirdPersonDistance", "ThirdPersonZoom", "thirdPersonZoom", "CameraDistance", "cameraDistance", "TargetCameraDistance", "targetCameraDistance",
			"DesiredCameraDistance", "desiredCameraDistance", "CameraZoom", "cameraZoom", "TargetCameraZoom", "targetCameraZoom", "tpCameraDistance", "TpCameraDistance", "tpCameraZoom", "TpCameraZoom"
		};
		string[] array2 = array;
		foreach (string name in array2)
		{
			flag |= SetMemberIfExists(target, name, distance);
		}
		return flag;
	}

	private static bool TrySetDistanceSetting(object? settings, float distance)
	{
		if (settings == null)
		{
			return false;
		}
		bool flag = false;
		string[] array = new string[8] { "thirdPersonCameraDistance", "ThirdPersonCameraDistance", "thirdPersonDistance", "ThirdPersonDistance", "tpCameraDistance", "TpCameraDistance", "cameraDistance", "CameraDistance" };
		object obj = GetMemberValue(settings, "Float") ?? GetMemberValue(settings, "float") ?? GetMemberValue(settings, "Floats") ?? GetMemberValue(settings, "floats");
		if (obj != null)
		{
			string[] array2 = array;
			foreach (string key in array2)
			{
				flag |= TrySetIndexerValue(obj, key, distance);
			}
		}
		string[] array3 = array;
		foreach (string key2 in array3)
		{
			flag |= TryInvokeSetMethod(settings, key2, distance);
		}
		return flag;
	}

	private static bool TrySetIndexerValue(object target, string key, float value)
	{
		try
		{
			PropertyInfo propertyInfo = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((PropertyInfo prop) => prop.GetIndexParameters().Length == 1 && prop.CanWrite);
			if (propertyInfo == null)
			{
				return false;
			}
			ParameterInfo parameterInfo = propertyInfo.GetIndexParameters()[0];
			object obj = ((parameterInfo.ParameterType == typeof(string)) ? key : key);
			propertyInfo.SetValue(target, ConvertValue(value, propertyInfo.PropertyType), new object[1] { obj });
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
			MethodInfo methodInfo = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault((MethodInfo m) =>
			{
				if (!m.Name.Contains("Set", StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				ParameterInfo[] parameters2 = m.GetParameters();
				return parameters2.Length == 2 && parameters2[0].ParameterType == typeof(string);
			});
			if (methodInfo == null)
			{
				return false;
			}
			ParameterInfo[] parameters = methodInfo.GetParameters();
			object obj = ConvertValue(value, parameters[1].ParameterType);
			methodInfo.Invoke(target, new object[2] { key, obj });
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void LogLikelyCameraMembers(object? target, string label)
	{
		if (capi == null || target == null)
		{
			return;
		}
		try
		{
			Type type = target.GetType();
			IEnumerable<string> first = from p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				where p.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
				select "property " + p.Name + ":" + p.PropertyType.Name;
			IEnumerable<string> second = from f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				where f.Name.Contains("Camera", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("Zoom", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
				select "field " + f.Name + ":" + f.FieldType.Name;
			capi.Logger.Notification("[ServerEssentials/AFK] {0} likely camera members: {1}", label, string.Join(", ", first.Concat(second).Take(40)));
		}
		catch
		{
		}
	}

	private void ApplyCameraOffset(double distance, double height, double yawRadians, bool staticOffset)
	{
		if (capi == null)
		{
			return;
		}
		object obj = capi.Render?.CameraOffset;
		if (obj != null)
		{
			double num = (staticOffset ? 0.0 : (Math.Sin(yawRadians) * distance));
			double num2 = (staticOffset ? (0.0 - distance) : (Math.Cos(yawRadians) * distance));
			double num3 = height;
			if (currentCameraPacket.PreventCameraGroundClip)
			{
				num3 = Math.Max(num3, currentCameraPacket.MinimumCameraHeightAbovePlayer);
			}
			bool flag = false;
			flag |= SetMemberIfExists(obj, "Translation", new Vec3f((float)num, (float)num3, (float)num2));
			flag |= SetMemberIfExists(obj, "translation", new Vec3f((float)num, (float)num3, (float)num2));
			flag |= SetMemberIfExists(obj, "TranslationX", (float)num);
			flag |= SetMemberIfExists(obj, "TranslationY", (float)num3);
			flag |= SetMemberIfExists(obj, "TranslationZ", (float)num2);
			flag |= SetMemberIfExists(obj, "translationX", (float)num);
			flag |= SetMemberIfExists(obj, "translationY", (float)num3);
			flag |= SetMemberIfExists(obj, "translationZ", (float)num2);
			flag |= SetMemberIfExists(obj, "Origin", new Vec3f(0f, 0f, 0f));
			if (!(flag | SetMemberIfExists(obj, "origin", new Vec3f(0f, 0f, 0f))) && currentCameraPacket.DebugClientCameraReflection)
			{
				capi.Logger.Warning("[ServerEssentials/AFK] CameraOffset distance write failed. CameraDistance cannot work until the transform member name is found.");
			}
			if (currentCameraPacket.DebugClientCameraReflection)
			{
				capi.Logger.Notification("[ServerEssentials/AFK] Applied camera offset x={0:0.00}, y={1:0.00}, z={2:0.00}", num, num3, num2);
			}
		}
	}

	private void RestoreCameraOffset()
	{
		if (capi != null)
		{
			object obj = capi.Render?.CameraOffset;
			if (obj != null)
			{
				SetMemberIfExists(obj, "Translation", new Vec3f(0f, 0f, 0f));
				SetMemberIfExists(obj, "translation", new Vec3f(0f, 0f, 0f));
				SetMemberIfExists(obj, "TranslationX", 0f);
				SetMemberIfExists(obj, "TranslationY", 0f);
				SetMemberIfExists(obj, "TranslationZ", 0f);
				SetMemberIfExists(obj, "translationX", 0f);
				SetMemberIfExists(obj, "translationY", 0f);
				SetMemberIfExists(obj, "translationZ", 0f);
			}
		}
	}

	private void TryForceThirdPersonCamera()
	{
		if (capi == null)
		{
			return;
		}
		try
		{
			object player = capi.World.Player;
			object mainCameraObject = GetMainCameraObject();
			if (mainCameraObject != null)
			{
				Type cameraModeMemberType = GetCameraModeMemberType(mainCameraObject);
				object obj = ((cameraModeMemberType == null) ? FindEnumValue("Vintagestory.API.Client.EnumCameraMode", "ThirdPerson", "Overhead", "ThirdPersonOverhead", "ThirdPersonBack", "ThirdPersonFront") : FindEnumValue(cameraModeMemberType, "ThirdPerson", "Overhead", "ThirdPersonOverhead", "ThirdPersonBack", "ThirdPersonFront"));
				if (obj != null && SetMainCameraMode(obj))
				{
					return;
				}
			}
			object obj2 = FindEnumValue("Vintagestory.API.Client.EnumCameraMode", "ThirdPerson", "Overhead", "ThirdPersonOverhead", "ThirdPersonBack", "ThirdPersonFront");
			if (obj2 != null)
			{
				SetMemberIfExists(player, "CameraMode", obj2);
				SetMemberIfExists(player, "cameraMode", obj2);
				SetMemberIfExists(player, "cameraModeInternal", obj2);
				SetMemberIfExists(player, "<CameraMode>k__BackingField", obj2);
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
		if (capi == null)
		{
			return false;
		}
		object memberValue = GetMemberValue(capi.Render, "CameraType");
		if (IsThirdPersonMode(memberValue))
		{
			return true;
		}
		object obj = capi.World?.Player?.Entity;
		object mode = ((obj == null) ? null : GetMemberValue(obj, "CameraMode"));
		if (IsThirdPersonMode(mode))
		{
			return true;
		}
		object currentMainCameraMode = GetCurrentMainCameraMode();
		return IsThirdPersonMode(currentMainCameraMode);
	}

	private static bool IsThirdPersonMode(object? mode)
	{
		if (mode == null)
		{
			return false;
		}
		string text = mode.ToString() ?? string.Empty;
		return text.Contains("Third", StringComparison.OrdinalIgnoreCase) || text.Contains("Overhead", StringComparison.OrdinalIgnoreCase);
	}

	private object? GetMainCameraObject()
	{
		if (capi == null)
		{
			return null;
		}
		object world = capi.World;
		if (world == null)
		{
			return null;
		}
		return GetMemberValue(world, "MainCamera") ?? GetMemberValue(world, "mainCamera") ?? GetMemberValue(capi, "MainCamera") ?? GetMemberValue(capi, "mainCamera");
	}

	private object? GetCurrentMainCameraMode()
	{
		object mainCameraObject = GetMainCameraObject();
		if (mainCameraObject == null)
		{
			return null;
		}
		return GetMemberValue(mainCameraObject, "CameraMode") ?? GetMemberValue(mainCameraObject, "cameraMode") ?? GetMemberValue(mainCameraObject, "mode");
	}

	private bool SetMainCameraMode(object modeValue)
	{
		object mainCameraObject = GetMainCameraObject();
		if (mainCameraObject == null)
		{
			return false;
		}
		return SetMemberIfExists(mainCameraObject, "CameraMode", modeValue) || SetMemberIfExists(mainCameraObject, "cameraMode", modeValue) || SetMemberIfExists(mainCameraObject, "mode", modeValue) || SetMemberIfExists(mainCameraObject, "<CameraMode>k__BackingField", modeValue);
	}

	private static Type? GetCameraModeMemberType(object camera)
	{
		Type type = camera.GetType();
		PropertyInfo propertyInfo = type.GetProperty("CameraMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? type.GetProperty("cameraMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? type.GetProperty("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (propertyInfo != null)
		{
			return propertyInfo.PropertyType;
		}
		return (type.GetField("CameraMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? type.GetField("cameraMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? type.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? type.GetField("<CameraMode>k__BackingField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))?.FieldType;
	}

	private static object? FindEnumValue(Type enumType, params string[] preferredNames)
	{
		if (!enumType.IsEnum)
		{
			return null;
		}
		foreach (string value in preferredNames)
		{
			if (Enum.IsDefined(enumType, value))
			{
				return Enum.Parse(enumType, value);
			}
		}
		foreach (object value2 in Enum.GetValues(enumType))
		{
			string text = value2.ToString() ?? string.Empty;
			if (text.Contains("Third", StringComparison.OrdinalIgnoreCase) || text.Contains("Overhead", StringComparison.OrdinalIgnoreCase))
			{
				return value2;
			}
		}
		return null;
	}

	private static object? FindEnumValue(string enumFullName, params string[] preferredNames)
	{
		Type type = Type.GetType(enumFullName + ", VintagestoryAPI") ?? Type.GetType(enumFullName);
		if (type == null || !type.IsEnum)
		{
			return null;
		}
		foreach (string value in preferredNames)
		{
			if (Enum.IsDefined(type, value))
			{
				return Enum.Parse(type, value);
			}
		}
		foreach (object value2 in Enum.GetValues(type))
		{
			string text = value2.ToString() ?? string.Empty;
			if (text.Contains("Third", StringComparison.OrdinalIgnoreCase) || text.Contains("Overhead", StringComparison.OrdinalIgnoreCase))
			{
				return value2;
			}
		}
		return null;
	}

	private static bool SetMemberIfExists(object target, string name, object value)
	{
		Type type = target.GetType();
		try
		{
			PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if ((object)property != null && property.CanWrite)
			{
				object value2 = ConvertValue(value, property.PropertyType);
				property.SetValue(target, value2);
				return true;
			}
			FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				object value3 = ConvertValue(value, field.FieldType);
				field.SetValue(target, value3);
				return true;
			}
		}
		catch
		{
			return false;
		}
		return false;
	}

	private static object ConvertValue(object value, Type targetType)
	{
		if (value == null)
		{
			return value;
		}
		if (targetType.IsInstanceOfType(value))
		{
			return value;
		}
		if (targetType == typeof(float) && value is double num)
		{
			return (float)num;
		}
		if (targetType == typeof(double) && value is float num2)
		{
			return (double)num2;
		}
		if (value is Vec3f vec3f && LooksLikeVector3(targetType))
		{
			object obj = CreateVector3(targetType, vec3f.X, vec3f.Y, vec3f.Z);
			if (obj != null)
			{
				return obj;
			}
		}
		return value;
	}

	private static bool LooksLikeVector3(Type type)
	{
		string text = type.FullName ?? type.Name;
		return text.Contains("Vec3", StringComparison.OrdinalIgnoreCase) || text.Contains("FastVec3", StringComparison.OrdinalIgnoreCase);
	}

	private static object? CreateVector3(Type targetType, float x, float y, float z)
	{
		try
		{
			ConstructorInfo constructor = targetType.GetConstructor(new Type[3]
			{
				typeof(float),
				typeof(float),
				typeof(float)
			});
			if (constructor != null)
			{
				return constructor.Invoke(new object[3] { x, y, z });
			}
			object obj = Activator.CreateInstance(targetType);
			if (obj == null)
			{
				return null;
			}
			SetVectorMember(obj, "X", x);
			SetVectorMember(obj, "Y", y);
			SetVectorMember(obj, "Z", z);
			SetVectorMember(obj, "x", x);
			SetVectorMember(obj, "y", y);
			SetVectorMember(obj, "z", z);
			return obj;
		}
		catch
		{
			return null;
		}
	}

	private static void SetVectorMember(object target, string name, float value)
	{
		Type type = target.GetType();
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if ((object)property != null && property.CanWrite)
		{
			property.SetValue(target, value);
			return;
		}
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (field != null)
		{
			field.SetValue(target, value);
		}
	}
}
