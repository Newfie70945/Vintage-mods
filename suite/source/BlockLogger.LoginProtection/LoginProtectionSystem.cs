using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BlockLogger.Config;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.LoginProtection;

public sealed class LoginProtectionSystem : ModSystem
{
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

	private const string ConfigFile = "NewfiesLoginProtection.json";

	private const string LegacyConfigFile = "loginprotection.json";

	private ICoreServerAPI sapi = null;

	private LoginProtectionConfig config = new LoginProtectionConfig();

	private readonly Dictionary<string, ProtectionState> protectedPlayers = new Dictionary<string, ProtectionState>(StringComparer.Ordinal);

	private long protectionGeneration;

	private readonly Dictionary<string, RebindState> pendingRebinds = new Dictionary<string, RebindState>(StringComparer.Ordinal);

	private long rebindListenerId = -1L;

	private const int RebindIntervalMs = 250;

	private const int RebindWindowMs = 5000;

	private EventInfo? playerReadyEventInfo;

	private Delegate? playerReadyHandler;

	private bool playerReadyHooked;

	private bool playerNowPlayingFallbackHooked;

	private bool playerJoinHooked;

	private bool entityLifecycleHooked;

	private readonly HashSet<string> readyForActivation = new HashSet<string>(StringComparer.Ordinal);

	private long protectionGuardListenerId = -1L;

	private const int PlayerReadyFallbackDelayMs = 1500;

	private const int ProtectionGuardIntervalMs = 250;

	public static LoginProtectionSystem? Current { get; private set; }

	public LoginProtectionConfig Config => config;

	public int ActiveProtectedCount
	{
		get
		{
			PruneExpiredProtection();
			return protectedPlayers.Values.Count((ProtectionState x) => x.Phase == ProtectionPhase.Active && x.EndsUtcTicks > DateTime.UtcNow.Ticks);
		}
	}

	public int PendingProtectedCount
	{
		get
		{
			PruneExpiredProtection();
			return protectedPlayers.Values.Count((ProtectionState x) => x.Phase == ProtectionPhase.Waiting || x.Phase == ProtectionPhase.Armed);
		}
	}

	public override bool ShouldLoad(EnumAppSide side)
	{
		return side == EnumAppSide.Server;
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		sapi = api;
		if (!SuiteFeatureConfig.Load(api).LoginProtection)
		{
			api.Logger.Notification("[LoginProtection] Entire subsystem disabled by {0}; no join/respawn hooks or command registered.", "NewfiesServerEssentialsFeatures.json");
			return;
		}
		Current = this;
		LoadConfig();
		playerReadyHooked = TryHookPlayerReady();
		sapi.Event.PlayerJoin += OnPlayerJoin;
		playerJoinHooked = true;
		sapi.Event.PlayerNowPlaying += OnPlayerNowPlayingFallback;
		playerNowPlayingFallbackHooked = true;
		sapi.Event.OnEntitySpawn += OnPlayerEntityLifecycle;
		sapi.Event.OnEntityLoaded += OnPlayerEntityLifecycle;
		entityLifecycleHooked = true;
		if (!playerReadyHooked)
		{
			sapi.Logger.Warning("[LoginProtection] PlayerReady is unavailable in the loaded Vintage Story API; PlayerJoin + delayed PlayerNowPlaying reliability mode is active.");
		}
		sapi.Event.PlayerRespawn += OnPlayerRespawn;
		sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
		sapi.RegisterCommand("loginprot", "Configure Newfie's Login Protection", "/loginprot <seconds>|on|off|status", HandleCommand, "controlserver");
		sapi.Logger.Notification("[LoginProtection] integrated module started. Enabled={0}, Seconds={1}, Join={2}, Respawn={3}", config.Enabled, config.ProtectionSeconds, config.ProtectOnJoin, config.ProtectOnRespawn);
	}

	public override void Dispose()
	{
		if (Current == this)
		{
			Current = null;
		}
		if (sapi != null)
		{
			UnhookPlayerReady();
			if (playerJoinHooked)
			{
				try
				{
					sapi.Event.PlayerJoin -= OnPlayerJoin;
				}
				catch
				{
				}
				playerJoinHooked = false;
			}
			if (playerNowPlayingFallbackHooked)
			{
				try
				{
					sapi.Event.PlayerNowPlaying -= OnPlayerNowPlayingFallback;
				}
				catch
				{
				}
				playerNowPlayingFallbackHooked = false;
			}
			if (entityLifecycleHooked)
			{
				try
				{
					sapi.Event.OnEntitySpawn -= OnPlayerEntityLifecycle;
				}
				catch
				{
				}
				try
				{
					sapi.Event.OnEntityLoaded -= OnPlayerEntityLifecycle;
				}
				catch
				{
				}
				entityLifecycleHooked = false;
			}
			sapi.Event.PlayerRespawn -= OnPlayerRespawn;
			sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
		}
		if (rebindListenerId >= 0 && sapi != null)
		{
			try
			{
				sapi.Event.UnregisterGameTickListener(rebindListenerId);
			}
			catch
			{
			}
			rebindListenerId = -1L;
		}
		if (protectionGuardListenerId >= 0 && sapi != null)
		{
			try
			{
				sapi.Event.UnregisterGameTickListener(protectionGuardListenerId);
			}
			catch
			{
			}
			protectionGuardListenerId = -1L;
		}
		pendingRebinds.Clear();
		protectedPlayers.Clear();
		readyForActivation.Clear();
		base.Dispose();
	}

	private void LoadConfig()
	{
		LoginProtectionConfig loginProtectionConfig = ModConfigRecovery.LoadOrNull<LoginProtectionConfig>(sapi, "NewfiesLoginProtection.json", "LoginProtection");
		if (loginProtectionConfig == null)
		{
			loginProtectionConfig = ModConfigRecovery.LoadOrNull<LoginProtectionConfig>(sapi, "loginprotection.json", "LoginProtection");
			if (loginProtectionConfig != null)
			{
				sapi.Logger.Notification("[LoginProtection] Migrating legacy {0} to {1}.", "loginprotection.json", "NewfiesLoginProtection.json");
			}
		}
		if (loginProtectionConfig == null)
		{
			loginProtectionConfig = new LoginProtectionConfig();
		}
		loginProtectionConfig.SchemaVersion = Math.Max(2, loginProtectionConfig.SchemaVersion);
		loginProtectionConfig.Clamp();
		config = loginProtectionConfig;
		SaveConfig();
	}

	public void SaveConfig()
	{
		config.Clamp();
		SuiteConfigStorage.Store(sapi, config, "NewfiesLoginProtection.json");
	}

	private bool TryHookPlayerReady()
	{
		try
		{
			EventInfo eventInfo = typeof(IServerEventAPI).GetEvent("PlayerReady", BindingFlags.Instance | BindingFlags.Public);
			if ((object)eventInfo == null)
			{
				eventInfo = sapi.Event.GetType().GetEvent("PlayerReady", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			Type type = eventInfo?.EventHandlerType;
			MethodInfo method = GetType().GetMethod("OnPlayerReady", BindingFlags.Instance | BindingFlags.NonPublic);
			MethodInfo methodInfo = eventInfo?.GetAddMethod(nonPublic: true);
			if (eventInfo == null || type == null || method == null || methodInfo == null)
			{
				return false;
			}
			Delegate obj = method.CreateDelegate(type, this);
			methodInfo.Invoke(sapi.Event, new object[1] { obj });
			playerReadyEventInfo = eventInfo;
			playerReadyHandler = obj;
			return true;
		}
		catch (Exception ex)
		{
			sapi.Logger.Warning("[LoginProtection] Could not hook optional PlayerReady event ({0}: {1}).", ex.GetType().Name, ex.Message);
			playerReadyEventInfo = null;
			playerReadyHandler = null;
			return false;
		}
	}

	private void UnhookPlayerReady()
	{
		EventInfo eventInfo = playerReadyEventInfo;
		Delegate obj = playerReadyHandler;
		playerReadyEventInfo = null;
		playerReadyHandler = null;
		if (eventInfo == null || (object)obj == null || sapi == null)
		{
			return;
		}
		try
		{
			eventInfo.GetRemoveMethod(nonPublic: true)?.Invoke(sapi.Event, new object[1] { obj });
		}
		catch
		{
		}
	}

	private void OnPlayerJoin(IServerPlayer player)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			readyForActivation.Remove(player.PlayerUID);
			EnsureJoinProtection(player, "join");
		}
	}

	private void OnPlayerNowPlayingFallback(IServerPlayer player)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		EnsureJoinProtection(player, "now-playing");
		if (!playerReadyHooked)
		{
			MarkReadyAndActivate(player, "now-playing");
			return;
		}
		string uid = player.PlayerUID;
		sapi.Event.RegisterCallback((float _) =>
		{
			IServerPlayer serverPlayer = OnlinePlayerByUid(uid);
			if (serverPlayer != null && serverPlayer.ConnectionState == EnumClientState.Connected)
			{
				MarkReadyAndActivate(serverPlayer, "now-playing-fallback");
			}
		}, 1500);
	}

	private void OnPlayerReady(IServerPlayer player)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			EnsureJoinProtection(player, "ready");
			MarkReadyAndActivate(player, "ready");
		}
	}

	private void OnPlayerEntityLifecycle(Entity entity)
	{
		if (!(entity is EntityPlayer entityPlayer) || string.IsNullOrWhiteSpace(entityPlayer.PlayerUID))
		{
			return;
		}
		string playerUID = entityPlayer.PlayerUID;
		if (protectedPlayers.ContainsKey(playerUID) || pendingRebinds.ContainsKey(playerUID))
		{
			IServerPlayer serverPlayer = OnlinePlayerByUid(playerUID);
			if (serverPlayer != null)
			{
				QueueRebind(serverPlayer, recoverSwimming: false);
				TryActivateProtection(serverPlayer, "entity-ready");
			}
		}
	}

	private void EnsureJoinProtection(IServerPlayer player, string source)
	{
		if (!config.Enabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		if (config.RecoverSwimmingOnLogin)
		{
			QueueRebind(player, recoverSwimming: true);
		}
		if (!config.ProtectOnJoin)
		{
			return;
		}
		if (protectedPlayers.TryGetValue(player.PlayerUID, out ProtectionState value))
		{
			if (value.Phase == ProtectionPhase.Active && value.EndsUtcTicks > DateTime.UtcNow.Ticks)
			{
				QueueRebind(player, recoverSwimming: false);
				EnsureProtectionGuardListener();
				return;
			}
			ProtectionPhase phase = value.Phase;
			if ((uint)phase <= 1u)
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
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			readyForActivation.Add(player.PlayerUID);
			TryActivateProtection(player, source);
		}
	}

	private void TryRecoverSwimming(IServerPlayer player)
	{
		EntityPlayer entityPlayer = player?.Entity;
		if (entityPlayer == null || !entityPlayer.Alive)
		{
			return;
		}
		bool flag = entityPlayer.FeetInLiquid || entityPlayer.Swimming;
		if (!flag)
		{
			try
			{
				flag = entityPlayer.IsEyesSubmerged();
			}
			catch
			{
			}
		}
		if (flag)
		{
			entityPlayer.Swimming = true;
			entityPlayer.Controls.DetachedMode = true;
			entityPlayer.ServerControls.DetachedMode = true;
		}
	}

	private void OnPlayerRespawn(IServerPlayer player)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			if (config.Enabled && config.RecoverSwimmingOnLogin)
			{
				QueueRebind(player, recoverSwimming: true);
			}
			if (config.Enabled && config.ProtectOnRespawn)
			{
				readyForActivation.Add(player.PlayerUID);
				ArmProtection(player, "respawn", "respawn", readyImmediately: true);
			}
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		if (player != null)
		{
			protectedPlayers.Remove(player.PlayerUID);
			pendingRebinds.Remove(player.PlayerUID);
			readyForActivation.Remove(player.PlayerUID);
			StopRebindListenerIfIdle();
			StopProtectionGuardIfIdle();
		}
	}

	private bool EnsureBehavior(IServerPlayer player)
	{
		if (player?.Entity == null)
		{
			return false;
		}
		if (player.Entity.GetBehavior<LoginProtectionBehavior>() != null)
		{
			return true;
		}
		player.Entity.AddBehavior(new LoginProtectionBehavior(player.Entity, this));
		player.Entity.CacheServerBehaviors();
		return player.Entity.GetBehavior<LoginProtectionBehavior>() != null;
	}

	private void QueueRebind(IServerPlayer player, bool recoverSwimming)
	{
		if (player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (pendingRebinds.TryGetValue(player.PlayerUID, out RebindState value))
		{
			value.DeadlineUnixMilliseconds = Math.Max(value.DeadlineUnixMilliseconds, num + 5000);
			value.RecoverSwimming |= recoverSwimming;
		}
		else
		{
			pendingRebinds[player.PlayerUID] = new RebindState
			{
				DeadlineUnixMilliseconds = num + 5000,
				RecoverSwimming = recoverSwimming,
				NextSwimmingCheckUnixMilliseconds = num
			};
		}
		ProcessRebindForPlayer(player.PlayerUID, num);
		if (rebindListenerId < 0 && pendingRebinds.Count > 0)
		{
			rebindListenerId = sapi.Event.RegisterGameTickListener((float _) =>
			{
				ProcessPendingRebinds();
			}, (Exception ex) =>
			{
				sapi.Logger.Warning("[LoginProtection] entity rebind pass failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}, 250);
		}
	}

	private void ProcessPendingRebinds()
	{
		long timestamp = Stopwatch.GetTimestamp();
		if (pendingRebinds.Count == 0)
		{
			StopRebindListenerIfIdle();
			return;
		}
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string[] array = pendingRebinds.Keys.ToArray();
		foreach (string uid in array)
		{
			ProcessRebindForPlayer(uid, now);
		}
		StopRebindListenerIfIdle();
		double milliseconds = (double)(Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency * 1000.0;
		SuiteTimingRegistry.Record("Login Protection rebind", milliseconds);
	}

	private void ProcessRebindForPlayer(string uid, long now)
	{
		if (!pendingRebinds.TryGetValue(uid, out RebindState value))
		{
			return;
		}
		IServerPlayer serverPlayer = OnlinePlayerByUid(uid);
		if (serverPlayer != null && serverPlayer.Entity != null)
		{
			if (config.Enabled && EnsureBehavior(serverPlayer))
			{
				value.SawBehavior = true;
				if (protectedPlayers.TryGetValue(uid, out ProtectionState value2) && value2.Phase == ProtectionPhase.Waiting)
				{
					value2.Phase = ProtectionPhase.Armed;
				}
				TryActivateProtection(serverPlayer, "rebind");
			}
			if (value.RecoverSwimming && value.SwimmingAttempts < 2 && now >= value.NextSwimmingCheckUnixMilliseconds)
			{
				TryRecoverSwimming(serverPlayer);
				value.SwimmingAttempts++;
				value.NextSwimmingCheckUnixMilliseconds = now + 250;
			}
		}
		if (now >= value.DeadlineUnixMilliseconds)
		{
			if (!value.SawBehavior && protectedPlayers.TryGetValue(uid, out ProtectionState value3) && (value3.EndsUtcTicks == 0L || value3.EndsUtcTicks > DateTime.UtcNow.Ticks) && !value.WarningLogged)
			{
				value.WarningLogged = true;
				sapi.Logger.Warning("[LoginProtection] protection remained armed for {0} but no current player entity received the damage behavior during the initialization window.", value3.PlayerName);
			}
			pendingRebinds.Remove(uid);
		}
	}

	private void StopRebindListenerIfIdle()
	{
		if (pendingRebinds.Count == 0 && rebindListenerId >= 0)
		{
			try
			{
				sapi.Event.UnregisterGameTickListener(rebindListenerId);
			}
			catch
			{
			}
			rebindListenerId = -1L;
		}
	}

	private void ArmProtection(IServerPlayer player, string reason, string source, bool readyImmediately)
	{
		if (!config.Enabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID))
		{
			return;
		}
		long ticks = DateTime.UtcNow.Ticks;
		if (!string.Equals(reason, "respawn", StringComparison.OrdinalIgnoreCase) && protectedPlayers.TryGetValue(player.PlayerUID, out ProtectionState value) && value.Phase == ProtectionPhase.Active && value.EndsUtcTicks > ticks)
		{
			QueueRebind(player, recoverSwimming: false);
			return;
		}
		long generation = ++protectionGeneration;
		protectedPlayers[player.PlayerUID] = new ProtectionState(generation, player.PlayerName, reason)
		{
			Phase = ((player.Entity != null) ? ProtectionPhase.Armed : ProtectionPhase.Waiting)
		};
		if (readyImmediately)
		{
			readyForActivation.Add(player.PlayerUID);
		}
		QueueRebind(player, recoverSwimming: false);
		EnsureProtectionGuardListener();
		TryActivateProtection(player, source);
		sapi.Logger.Debug("[LoginProtection] Armed {0} protection for {1} from {2}; countdown waits for a ready, bound player entity.", reason, player.PlayerName, source);
	}

	private void TryActivateProtection(IServerPlayer player, string source)
	{
		if (!config.Enabled || player == null || string.IsNullOrWhiteSpace(player.PlayerUID) || !readyForActivation.Contains(player.PlayerUID) || player.ConnectionState != EnumClientState.Connected || player.Entity == null || !player.Entity.Alive || !protectedPlayers.TryGetValue(player.PlayerUID, out ProtectionState value))
		{
			return;
		}
		if (value.Phase == ProtectionPhase.Active && value.EndsUtcTicks > DateTime.UtcNow.Ticks)
		{
			EnsureBehavior(player);
		}
		else if (EnsureBehavior(player))
		{
			int num = Math.Clamp(config.ProtectionSeconds, 1, 3600);
			value.Phase = ProtectionPhase.Active;
			value.ActivatedUtcTicks = DateTime.UtcNow.Ticks;
			value.EndsUtcTicks = DateTime.UtcNow.AddSeconds(num).Ticks;
			if (config.ExtinguishFire)
			{
				player.Entity.IsOnFire = false;
			}
			if (!value.StartAnnounced && config.AnnounceOnJoin)
			{
				value.StartAnnounced = true;
				SendPlayerMessage(player, (value.Reason == "respawn") ? $"Respawn protection active for {num}s." : $"Login protection active for {num}s.");
			}
			long generation = value.Generation;
			sapi.Event.RegisterCallback((float _) =>
			{
				ExpireProtection(player.PlayerUID, generation);
			}, num * 1000);
			sapi.Logger.Debug("[LoginProtection] Activated {0} protection for {1} from {2}; full {3}s starts now.", value.Reason, player.PlayerName, source, num);
		}
	}

	private void ExpireProtection(string playerUid, long generation)
	{
		if (!protectedPlayers.TryGetValue(playerUid, out ProtectionState value) || value.Generation != generation)
		{
			return;
		}
		value.Phase = ProtectionPhase.Expired;
		protectedPlayers.Remove(playerUid);
		StopProtectionGuardIfIdle();
		if (config.Enabled && config.AnnounceOnEnd)
		{
			IServerPlayer serverPlayer = OnlinePlayerByUid(playerUid);
			if (serverPlayer != null)
			{
				SendPlayerMessage(serverPlayer, "Login protection disabled — you can now take damage.");
			}
		}
	}

	private void CancelProtection(string playerUid, string message, bool announce)
	{
		if (!protectedPlayers.Remove(playerUid))
		{
			return;
		}
		StopProtectionGuardIfIdle();
		if (announce)
		{
			IServerPlayer serverPlayer = OnlinePlayerByUid(playerUid);
			if (serverPlayer != null)
			{
				SendPlayerMessage(serverPlayer, message);
			}
		}
	}

	public bool IsProtected(string playerUid)
	{
		if (!config.Enabled || string.IsNullOrWhiteSpace(playerUid))
		{
			return false;
		}
		if (!protectedPlayers.TryGetValue(playerUid, out ProtectionState value))
		{
			return false;
		}
		if (value.Phase != ProtectionPhase.Active || value.EndsUtcTicks <= 0)
		{
			return false;
		}
		if (DateTime.UtcNow.Ticks < value.EndsUtcTicks)
		{
			return true;
		}
		value.Phase = ProtectionPhase.Expired;
		protectedPlayers.Remove(playerUid);
		StopProtectionGuardIfIdle();
		if (config.AnnounceOnEnd)
		{
			IServerPlayer serverPlayer = OnlinePlayerByUid(playerUid);
			if (serverPlayer != null)
			{
				SendPlayerMessage(serverPlayer, "Login protection disabled — you can now take damage.");
			}
		}
		return false;
	}

	public bool ShouldBlockIncomingDamage(EntityPlayer target, DamageSource source)
	{
		if (!config.Enabled || target == null)
		{
			return false;
		}
		string text = target.PlayerUID ?? string.Empty;
		bool flag = IsProtected(text);
		Entity entity = null;
		try
		{
			entity = source?.GetCauseEntity();
		}
		catch
		{
		}
		if (entity is EntityPlayer entityPlayer && !string.Equals(entityPlayer.PlayerUID, text, StringComparison.Ordinal) && IsProtected(entityPlayer.PlayerUID) && config.BlockPvP)
		{
			if (config.CancelOnAttack)
			{
				CancelProtection(entityPlayer.PlayerUID, "Login protection cancelled because you attacked another player.", announce: true);
			}
			return true;
		}
		if (!flag)
		{
			return false;
		}
		if (source != null && source.Source == EnumDamageSource.Fall && !config.ProtectFallDamage)
		{
			return false;
		}
		if (config.ExtinguishFire && target.IsOnFire)
		{
			target.IsOnFire = false;
		}
		return true;
	}

	public void OnProtectedPlayerAttacked(EntityPlayer attacker)
	{
		if (config.Enabled && config.CancelOnAttack && attacker != null && IsProtected(attacker.PlayerUID))
		{
			CancelProtection(attacker.PlayerUID, "Login protection cancelled because you attacked.", announce: true);
		}
	}

	public List<string> GetGuiStatusLines()
	{
		PruneExpiredProtection();
		List<string> list = new List<string>
		{
			"Login Protection: " + (config.Enabled ? "ENABLED" : "DISABLED"),
			$"Protection duration: {config.ProtectionSeconds}s",
			"Protect on join: " + (config.ProtectOnJoin ? "ON" : "OFF"),
			"Protect on respawn: " + (config.ProtectOnRespawn ? "ON" : "OFF"),
			"Block PvP while protected: " + (config.BlockPvP ? "ON" : "OFF"),
			"Cancel on attack: " + (config.CancelOnAttack ? "ON" : "OFF"),
			"Protect fall damage: " + (config.ProtectFallDamage ? "ON" : "OFF"),
			"Extinguish fire: " + (config.ExtinguishFire ? "ON" : "OFF"),
			"Water-login swim recovery: " + (config.RecoverSwimmingOnLogin ? "ON" : "OFF"),
			$"Currently protected: {ActiveProtectedCount}",
			$"Waiting for ready entity: {PendingProtectedCount}",
			"",
			"Protected players:"
		};
		long now = DateTime.UtcNow.Ticks;
		foreach (KeyValuePair<string, ProtectionState> item in (from x in protectedPlayers
			where x.Value.Phase == ProtectionPhase.Active && x.Value.EndsUtcTicks > now
			orderby x.Value.EndsUtcTicks
			select x).Take(40))
		{
			int value = Math.Max(0, (int)Math.Ceiling(TimeSpan.FromTicks(item.Value.EndsUtcTicks - now).TotalSeconds));
			list.Add($"  {item.Value.PlayerName}: {value}s remaining");
		}
		foreach (KeyValuePair<string, ProtectionState> item2 in protectedPlayers.Where((KeyValuePair<string, ProtectionState> x) =>
		{
			ProtectionPhase phase = x.Value.Phase;
			return (uint)phase <= 1u;
		}).Take(40))
		{
			list.Add("  " + item2.Value.PlayerName + ": waiting for player-ready/entity bind");
		}
		if (protectedPlayers.Count == 0)
		{
			list.Add("  (none)");
		}
		return list;
	}

	public List<string> GetGuiSettingsLines()
	{
		return new List<string>
		{
			"Login Protection: " + (config.Enabled ? "ENABLED" : "DISABLED"),
			$"Protection duration: {config.ProtectionSeconds}s",
			$"Currently protected: {ActiveProtectedCount}",
			"",
			"Changes are saved immediately to NewfiesLoginProtection.json."
		};
	}

	public string SetBooleanSettingFromGui(string? key, bool value)
	{
		switch ((key ?? "").Trim().ToLowerInvariant())
		{
		case "enabled":
		{
			bool enabled = config.Enabled;
			config.Enabled = value;
			if (!value & enabled)
			{
				DisableAllProtection();
			}
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
			return "Unknown Login Protection setting '" + key + "'.";
		}
		SaveConfig();
		return "Login Protection setting saved.";
	}

	public string SetIntegerSettingFromGui(string? key, int value)
	{
		string text = (key ?? "").Trim().ToLowerInvariant();
		if (text != "protectionseconds")
		{
			return "Unknown Login Protection setting '" + key + "'.";
		}
		config.ProtectionSeconds = Math.Clamp(value, 1, 3600);
		SaveConfig();
		return $"Protection duration set to {config.ProtectionSeconds}s.";
	}

	private void DisableAllProtection()
	{
		List<IServerPlayer> list = (from p in sapi.World.AllOnlinePlayers.OfType<IServerPlayer>()
			where protectedPlayers.ContainsKey(p.PlayerUID)
			select p).ToList();
		protectedPlayers.Clear();
		readyForActivation.Clear();
		StopProtectionGuardIfIdle();
		foreach (IServerPlayer item in list)
		{
			SendPlayerMessage(item, "Login protection disabled by an administrator.");
		}
	}

	private void PruneExpiredProtection()
	{
		if (protectedPlayers.Count == 0)
		{
			return;
		}
		long now = DateTime.UtcNow.Ticks;
		foreach (string item in (from x in protectedPlayers
			where x.Value.Phase == ProtectionPhase.Active && x.Value.EndsUtcTicks > 0 && x.Value.EndsUtcTicks <= now
			select x.Key).ToList())
		{
			protectedPlayers.Remove(item);
		}
		StopProtectionGuardIfIdle();
	}

	private void EnsureProtectionGuardListener()
	{
		if (protectionGuardListenerId < 0 && protectedPlayers.Count != 0)
		{
			protectionGuardListenerId = sapi.Event.RegisterGameTickListener((float _) =>
			{
				ProtectionGuardPass();
			}, 250);
		}
	}

	private void ProtectionGuardPass()
	{
		PruneExpiredProtection();
		if (protectedPlayers.Count == 0)
		{
			StopProtectionGuardIfIdle();
			return;
		}
		string[] array = protectedPlayers.Keys.ToArray();
		foreach (string text in array)
		{
			IServerPlayer serverPlayer = OnlinePlayerByUid(text);
			if (serverPlayer?.Entity != null)
			{
				if (EnsureBehavior(serverPlayer) && protectedPlayers.TryGetValue(text, out ProtectionState value) && value.Phase == ProtectionPhase.Waiting)
				{
					value.Phase = ProtectionPhase.Armed;
				}
				TryActivateProtection(serverPlayer, "guard");
			}
		}
	}

	private void StopProtectionGuardIfIdle()
	{
		if (protectedPlayers.Count == 0 && protectionGuardListenerId >= 0 && sapi != null)
		{
			try
			{
				sapi.Event.UnregisterGameTickListener(protectionGuardListenerId);
			}
			catch
			{
			}
			protectionGuardListenerId = -1L;
		}
	}

	private IServerPlayer? OnlinePlayerByUid(string playerUid)
	{
		return sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerUID, playerUid, StringComparison.Ordinal));
	}

	private void SendPlayerMessage(IServerPlayer player, string text)
	{
		sapi.SendMessage(player, GlobalConstants.GeneralChatGroup, text, EnumChatType.Notification);
	}

	private void HandleCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = (args.PopWord() ?? "").Trim();
		int result;
		if (text.Length == 0 || text.Equals("status", StringComparison.OrdinalIgnoreCase))
		{
			sapi.SendMessage(player, groupId, $"Login Protection: {(config.Enabled ? "ON" : "OFF")}; {config.ProtectionSeconds}s; active={ActiveProtectedCount}; join={config.ProtectOnJoin}; respawn={config.ProtectOnRespawn}", EnumChatType.CommandSuccess);
		}
		else if (text.Equals("on", StringComparison.OrdinalIgnoreCase))
		{
			SetBooleanSettingFromGui("enabled", value: true);
			sapi.SendMessage(player, groupId, "Login Protection enabled.", EnumChatType.CommandSuccess);
		}
		else if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			SetBooleanSettingFromGui("enabled", value: false);
			sapi.SendMessage(player, groupId, "Login Protection disabled.", EnumChatType.CommandSuccess);
		}
		else if (!int.TryParse(text, out result))
		{
			sapi.SendMessage(player, groupId, "Usage: /loginprot <seconds>|on|off|status", EnumChatType.CommandError);
		}
		else
		{
			config.ProtectionSeconds = Math.Clamp(result, 1, 3600);
			SaveConfig();
			sapi.SendMessage(player, groupId, $"Login protection duration set to {config.ProtectionSeconds}s.", EnumChatType.CommandSuccess);
		}
	}
}
