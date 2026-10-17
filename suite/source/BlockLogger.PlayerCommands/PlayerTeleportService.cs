using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BlockLogger.AntiCheat;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerTeleportService : IDisposable
{
	private sealed class PendingTeleport
	{
		public string PlayerUid { get; init; } = "";

		public string Kind { get; init; } = "";

		public string Label { get; init; } = "";

		public long DueUtcMs { get; init; }

		public double StartX { get; init; }

		public double StartY { get; init; }

		public double StartZ { get; init; }

		public bool CancelOnMove { get; init; }

		public PlayerTeleportPosition Destination { get; init; } = new PlayerTeleportPosition();

		public bool CostEnabled { get; init; }

		public string CostItemCode { get; init; } = "";

		public int CostQuantity { get; init; }

		public string CooldownKey { get; init; } = "";

		public int CooldownSeconds { get; init; }

		public bool SwapBackAfterTeleport { get; init; }
	}

	private sealed class PendingConsentTeleportRequest
	{
		public string Kind { get; init; } = "";

		public string RequesterUid { get; init; } = "";

		public string RequesterName { get; init; } = "";

		public string TargetUid { get; init; } = "";

		public string TargetName { get; init; } = "";

		public long ExpiresUtcMs { get; init; }

		public bool Accepted { get; set; }

		public long DueUtcMs { get; set; }
	}

	private const int ConsentRequestTimeoutSeconds = 60;

	private const int ConsentAcceptWarmupSeconds = 10;

	private const string SaveKey = "newfiesserveressentials:player-teleports-v1";

	private readonly ICoreServerAPI api;

	private readonly Func<PlayerCommandConfig> getConfig;

	private readonly System.Func<string, bool>? isJailed;

	private readonly Dictionary<string, PlayerTeleportProfile> profiles = new Dictionary<string, PlayerTeleportProfile>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PendingTeleport> pendingByUid = new Dictionary<string, PendingTeleport>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PendingConsentTeleportRequest> consentIncomingByTargetUid = new Dictionary<string, PendingConsentTeleportRequest>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, PendingConsentTeleportRequest> consentOutgoingByRequesterUid = new Dictionary<string, PendingConsentTeleportRequest>(StringComparer.OrdinalIgnoreCase);

	private readonly Dictionary<string, long> cooldownUtc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> rtpSearching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private long tickListenerId;

	private bool disposed;

	private bool storageReady;

	private bool dirty;

	private long lastFlushUtcMs;

	private long lastCooldownPruneUtcMs;

	public bool StorageReady => storageReady;

	public string StorageError { get; private set; } = "";

	public PlayerTeleportService(ICoreServerAPI api, Func<PlayerCommandConfig> getConfig, System.Func<string, bool>? isJailed = null)
	{
		this.api = api;
		this.getConfig = getConfig;
		this.isJailed = isJailed;
	}

	public bool HasPending(string playerUid)
	{
		return !string.IsNullOrWhiteSpace(playerUid) && (pendingByUid.ContainsKey(playerUid) || rtpSearching.Contains(playerUid) || HasAcceptedConsentPlayer(playerUid));
	}

	public void Start()
	{
		Load();
		api.Event.PlayerDeath += OnPlayerDeath;
		api.Event.PlayerDisconnect += OnPlayerDisconnect;
		api.Event.GameWorldSave += OnWorldSave;
		tickListenerId = api.Event.RegisterGameTickListener((float _) =>
		{
			Tick();
		}, 250);
	}

	public void RecordTeleportOrigin(IServerPlayer player, EntityPos? origin, string reason)
	{
		if (player != null && !string.IsNullOrWhiteSpace(player.PlayerUID) && origin != null)
		{
			PlayerCommandConfig playerCommandConfig = getConfig();
			if (playerCommandConfig.BackEnabled && playerCommandConfig.BackTrackTeleports)
			{
				SetBack(player, PlayerTeleportPosition.From(origin), reason);
			}
		}
	}

	public void RecordTeleportOrigin(IServerPlayer player, string reason)
	{
		RecordTeleportOrigin(player, EntityRuntimeCompat.GetPos(player.Entity)?.Copy(), reason);
	}

	public void HandleSpawn(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (IsJailed(player))
		{
			Reply(player, groupId, "You cannot use /spawn while jailed.", success: false);
		}
		else
		{
			if (!TryResolveTeleportPolicy(player, "spawn", playerCommandConfig.SpawnCooldownSeconds, groupId, out var cooldownSeconds))
			{
				return;
			}
			if (!CheckCooldown(player, "spawn", cooldownSeconds, out var remainingSeconds))
			{
				Reply(player, groupId, $"You can use /spawn again in {remainingSeconds} second(s).", success: false);
				return;
			}
			EntityPos entityPos = api.World.DefaultSpawnPosition?.Copy();
			if (entityPos == null)
			{
				Reply(player, groupId, "The server did not provide a global world spawn position.", success: false);
				return;
			}
			BeginTeleport(player, groupId, new PendingTeleport
			{
				PlayerUid = player.PlayerUID,
				Kind = "spawn",
				Label = "world spawn",
				Destination = PlayerTeleportPosition.From(entityPos),
				CancelOnMove = playerCommandConfig.SpawnCancelOnMove,
				DueUtcMs = NowMs() + (long)playerCommandConfig.SpawnWarmupSeconds * 1000L,
				CooldownKey = "spawn",
				CooldownSeconds = cooldownSeconds
			}, playerCommandConfig.SpawnWarmupSeconds);
		}
	}

	public void HandleBack(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		int cooldownSeconds;
		if (IsJailed(player))
		{
			Reply(player, groupId, "You cannot use /back while jailed.", success: false);
		}
		else if (!playerCommandConfig.BackEnabled)
		{
			Reply(player, groupId, "The Server Essentials /back command is disabled.", success: false);
		}
		else if (TryResolveTeleportPolicy(player, "back", playerCommandConfig.BackCooldownSeconds, groupId, out cooldownSeconds))
		{
			PlayerTeleportProfile playerTeleportProfile = Profile(player);
			if (playerTeleportProfile.Back == null)
			{
				Reply(player, groupId, "You do not have a saved death or teleport position to return to.", success: false);
				return;
			}
			if (!CheckCooldown(player, "back", cooldownSeconds, out var remainingSeconds))
			{
				Reply(player, groupId, $"You can use /back again in {remainingSeconds} second(s).", success: false);
				return;
			}
			BeginTeleport(player, groupId, new PendingTeleport
			{
				PlayerUid = player.PlayerUID,
				Kind = "back",
				Label = (string.IsNullOrWhiteSpace(playerTeleportProfile.BackReason) ? "previous location" : playerTeleportProfile.BackReason),
				Destination = Clone(playerTeleportProfile.Back),
				CancelOnMove = playerCommandConfig.BackCancelOnMove,
				DueUtcMs = NowMs() + (long)playerCommandConfig.BackWarmupSeconds * 1000L,
				CooldownKey = "back",
				CooldownSeconds = cooldownSeconds,
				SwapBackAfterTeleport = true
			}, playerCommandConfig.BackWarmupSeconds);
		}
	}

	public void HandleSetHome(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!EnsureWritable(player, groupId))
		{
			return;
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!playerCommandConfig.HomesEnabled)
		{
			Reply(player, groupId, "Homes are disabled by Server Essentials.", success: false);
			return;
		}
		string value = (args.PopWord() ?? "home").Trim();
		string name = NormalizeHomeName(value);
		if (string.IsNullOrWhiteSpace(name))
		{
			Reply(player, groupId, "Home names may contain letters, numbers, '-' and '_' and must be 1-32 characters.", success: false);
			return;
		}
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		if (pos == null)
		{
			Reply(player, groupId, "Your current position could not be read safely.", success: false);
			return;
		}
		PlayerTeleportProfile playerTeleportProfile = Profile(player);
		PlayerHomeDefinition playerHomeDefinition = playerTeleportProfile.Homes.FirstOrDefault((PlayerHomeDefinition h) => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		int homeLimit = GetHomeLimit(player);
		if (playerHomeDefinition == null && playerTeleportProfile.Homes.Count >= homeLimit)
		{
			Reply(player, groupId, $"You have reached your home limit ({homeLimit}). Delete a home before creating another.", success: false);
			return;
		}
		List<PlayerHomeDefinition> homes = playerTeleportProfile.Homes.Select((PlayerHomeDefinition h) => new PlayerHomeDefinition
		{
			Name = h.Name,
			Position = Clone(h.Position),
			UpdatedAtUnixMilliseconds = h.UpdatedAtUnixMilliseconds
		}).ToList();
		if (playerHomeDefinition == null)
		{
			playerHomeDefinition = new PlayerHomeDefinition
			{
				Name = name
			};
			playerTeleportProfile.Homes.Add(playerHomeDefinition);
		}
		playerHomeDefinition.Name = name;
		playerHomeDefinition.Position = PlayerTeleportPosition.From(pos);
		playerHomeDefinition.UpdatedAtUnixMilliseconds = NowMs();
		playerTeleportProfile.LastKnownName = player.PlayerName;
		if (!Save())
		{
			playerTeleportProfile.Homes = homes;
			Reply(player, groupId, "Your home could not be saved. The previous homes were retained; please try again after the storage error is resolved.", success: false);
			return;
		}
		Reply(player, groupId, $"Home '{name}' saved at {pos.X:0.#}, {pos.Y:0.#}, {pos.Z:0.#}. ({playerTeleportProfile.Homes.Count}/{homeLimit})", success: true);
	}

	public void HandleHome(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		int cooldownSeconds;
		if (IsJailed(player))
		{
			Reply(player, groupId, "You cannot use /home while jailed.", success: false);
		}
		else if (!playerCommandConfig.HomesEnabled)
		{
			Reply(player, groupId, "Homes are disabled by Server Essentials.", success: false);
		}
		else if (TryResolveTeleportPolicy(player, "home", playerCommandConfig.HomeCooldownSeconds, groupId, out cooldownSeconds))
		{
			PlayerTeleportProfile profile = Profile(player);
			PlayerHomeDefinition playerHomeDefinition = ResolveHome(profile, (args.PopWord() ?? "").Trim(), out string error);
			if (playerHomeDefinition == null)
			{
				Reply(player, groupId, error, success: false);
				return;
			}
			if (!CheckCooldown(player, "home", cooldownSeconds, out var remainingSeconds))
			{
				Reply(player, groupId, $"You can use /home again in {remainingSeconds} second(s).", success: false);
				return;
			}
			BeginTeleport(player, groupId, new PendingTeleport
			{
				PlayerUid = player.PlayerUID,
				Kind = "home",
				Label = "home '" + playerHomeDefinition.Name + "'",
				Destination = Clone(playerHomeDefinition.Position),
				CancelOnMove = playerCommandConfig.HomeCancelOnMove,
				DueUtcMs = NowMs() + (long)playerCommandConfig.HomeWarmupSeconds * 1000L,
				CooldownKey = "home",
				CooldownSeconds = cooldownSeconds
			}, playerCommandConfig.HomeWarmupSeconds);
		}
	}

	public void HandleDeleteHome(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!EnsureWritable(player, groupId))
		{
			return;
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!playerCommandConfig.HomesEnabled)
		{
			Reply(player, groupId, "Homes are disabled by Server Essentials.", success: false);
			return;
		}
		PlayerTeleportProfile playerTeleportProfile = Profile(player);
		PlayerHomeDefinition playerHomeDefinition = ResolveHome(playerTeleportProfile, (args.PopWord() ?? "").Trim(), out string error);
		if (playerHomeDefinition == null)
		{
			Reply(player, groupId, error, success: false);
			return;
		}
		int index = playerTeleportProfile.Homes.IndexOf(playerHomeDefinition);
		playerTeleportProfile.Homes.Remove(playerHomeDefinition);
		if (!Save())
		{
			playerTeleportProfile.Homes.Insert(index, playerHomeDefinition);
			Reply(player, groupId, "Your home could not be deleted from storage. It has been retained; please try again after the storage error is resolved.", success: false);
		}
		else
		{
			Reply(player, groupId, "Home '" + playerHomeDefinition.Name + "' deleted.", success: true);
		}
	}

	public void HandleHomes(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!playerCommandConfig.HomesEnabled)
		{
			Reply(player, groupId, "Homes are disabled by Server Essentials.", success: false);
			return;
		}
		PlayerTeleportProfile playerTeleportProfile = Profile(player);
		int homeLimit = GetHomeLimit(player);
		if (playerTeleportProfile.Homes.Count == 0)
		{
			Reply(player, groupId, $"You have no homes. Use /sethome [name]. Home limit: {homeLimit}.", success: true);
			return;
		}
		string value = string.Join(", ", from h in playerTeleportProfile.Homes.OrderBy((PlayerHomeDefinition h) => h.Name, StringComparer.OrdinalIgnoreCase)
			select h.Name);
		Reply(player, groupId, $"Homes ({playerTeleportProfile.Homes.Count}/{homeLimit}): {value}", success: true);
	}

	public void HandleT2p(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (IsJailed(player))
		{
			Reply(player, groupId, "You cannot use /t2p while jailed.", success: false);
		}
		else if (!playerCommandConfig.T2pEnabled)
		{
			Reply(player, groupId, "The Server Essentials /t2p command is disabled.", success: false);
		}
		else
		{
			if (!TryResolveTeleportPolicy(player, "t2p", playerCommandConfig.T2pCooldownSeconds, groupId, out var cooldownSeconds, requireAdminWhenUnconfigured: true))
			{
				return;
			}
			if (!CheckCooldown(player, "t2p", cooldownSeconds, out var remainingSeconds))
			{
				Reply(player, groupId, $"You can use /t2p again in {remainingSeconds} second(s).", success: false);
				return;
			}
			if (pendingByUid.ContainsKey(player.PlayerUID) || rtpSearching.Contains(player.PlayerUID) || HasAcceptedConsentPlayer(player.PlayerUID))
			{
				Reply(player, groupId, "You already have a Suite teleport in progress.", success: false);
				return;
			}
			if (consentOutgoingByRequesterUid.TryGetValue(player.PlayerUID, out PendingConsentTeleportRequest value))
			{
				string value2 = OnlineByUid(value.TargetUid)?.PlayerName ?? value.TargetName;
				Reply(player, groupId, $"You already have a /{value.Kind} request pending for {value2}. Wait for it to be accepted or expire.", success: false);
				return;
			}
			string text = (args.PopAll() ?? "").Trim();
			if (text.Length == 0)
			{
				Reply(player, groupId, "Usage: /t2p <online player>", success: false);
				return;
			}
			IServerPlayer serverPlayer = OnlineByName(text);
			if (serverPlayer == null)
			{
				Reply(player, groupId, "Player '" + text + "' is not online.", success: false);
				return;
			}
			if (string.Equals(serverPlayer.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
			{
				Reply(player, groupId, "You cannot send yourself a /t2p request.", success: false);
				return;
			}
			if (IsJailed(serverPlayer))
			{
				Reply(player, groupId, serverPlayer.PlayerName + " is jailed and cannot accept /t2p requests.", success: false);
				return;
			}
			if (consentIncomingByTargetUid.TryGetValue(serverPlayer.PlayerUID, out PendingConsentTeleportRequest value3))
			{
				string value4 = OnlineByUid(value3.RequesterUid)?.PlayerName ?? value3.RequesterName;
				Reply(player, groupId, $"{serverPlayer.PlayerName} already has a /{value3.Kind} request from {value4} waiting for a response.", success: false);
				return;
			}
			long num = NowMs();
			PendingConsentTeleportRequest value5 = new PendingConsentTeleportRequest
			{
				Kind = "t2p",
				RequesterUid = player.PlayerUID,
				RequesterName = player.PlayerName,
				TargetUid = serverPlayer.PlayerUID,
				TargetName = serverPlayer.PlayerName,
				ExpiresUtcMs = num + 60000
			};
			consentOutgoingByRequesterUid[player.PlayerUID] = value5;
			consentIncomingByTargetUid[serverPlayer.PlayerUID] = value5;
			Reply(player, groupId, $"Teleport request sent to {serverPlayer.PlayerName}. They must use /tpaccept within {60} seconds. Your cooldown starts only if the teleport completes.", success: true);
			serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, $"{player.PlayerName} wants to teleport to you. Type /tpaccept within {60} seconds (or /nse tpaccept if another mod owns that command). After accepting, they will be teleported to you in {10} seconds.", EnumChatType.Notification);
		}
	}

	public void HandleT2m(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (IsJailed(player))
		{
			Reply(player, groupId, "You cannot use /t2m while jailed.", success: false);
		}
		else if (!playerCommandConfig.T2mEnabled)
		{
			Reply(player, groupId, "The Server Essentials /t2m command is disabled.", success: false);
		}
		else
		{
			if (!TryResolveTeleportPolicy(player, "t2m", playerCommandConfig.T2mCooldownSeconds, groupId, out var cooldownSeconds, requireAdminWhenUnconfigured: true))
			{
				return;
			}
			if (!CheckCooldown(player, "t2m", cooldownSeconds, out var remainingSeconds))
			{
				Reply(player, groupId, $"You can use /t2m again in {remainingSeconds} second(s).", success: false);
				return;
			}
			if (HasAcceptedConsentPlayer(player.PlayerUID))
			{
				Reply(player, groupId, "You already have an accepted /t2m teleport in progress.", success: false);
				return;
			}
			if (consentOutgoingByRequesterUid.TryGetValue(player.PlayerUID, out PendingConsentTeleportRequest value))
			{
				string text = OnlineByUid(value.TargetUid)?.PlayerName ?? value.TargetName;
				Reply(player, groupId, "You already have a /t2m request pending for " + text + ". Wait for it to be accepted or expire.", success: false);
				return;
			}
			string text2 = (args.PopAll() ?? "").Trim();
			if (text2.Length == 0)
			{
				Reply(player, groupId, "Usage: /t2m <online player>", success: false);
				return;
			}
			IServerPlayer serverPlayer = OnlineByName(text2);
			if (serverPlayer == null)
			{
				Reply(player, groupId, "Player '" + text2 + "' is not online.", success: false);
				return;
			}
			if (string.Equals(serverPlayer.PlayerUID, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
			{
				Reply(player, groupId, "You cannot send yourself a /t2m request.", success: false);
				return;
			}
			if (IsJailed(serverPlayer))
			{
				Reply(player, groupId, serverPlayer.PlayerName + " is jailed and cannot be moved with /t2m.", success: false);
				return;
			}
			if (pendingByUid.ContainsKey(serverPlayer.PlayerUID) || rtpSearching.Contains(serverPlayer.PlayerUID) || HasAcceptedConsentPlayer(serverPlayer.PlayerUID))
			{
				Reply(player, groupId, serverPlayer.PlayerName + " already has a Suite teleport in progress.", success: false);
				return;
			}
			if (consentIncomingByTargetUid.TryGetValue(serverPlayer.PlayerUID, out PendingConsentTeleportRequest value2))
			{
				string text3 = OnlineByUid(value2.RequesterUid)?.PlayerName ?? value2.RequesterName;
				Reply(player, groupId, serverPlayer.PlayerName + " already has a /t2m request from " + text3 + " waiting for a response.", success: false);
				return;
			}
			long num = NowMs();
			PendingConsentTeleportRequest value3 = new PendingConsentTeleportRequest
			{
				Kind = "t2m",
				RequesterUid = player.PlayerUID,
				RequesterName = player.PlayerName,
				TargetUid = serverPlayer.PlayerUID,
				TargetName = serverPlayer.PlayerName,
				ExpiresUtcMs = num + 60000
			};
			consentOutgoingByRequesterUid[player.PlayerUID] = value3;
			consentIncomingByTargetUid[serverPlayer.PlayerUID] = value3;
			Reply(player, groupId, $"Teleport request sent to {serverPlayer.PlayerName}. They must use /tpaccept within {60} seconds. Your cooldown starts only if the teleport completes.", success: true);
			serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, $"{player.PlayerName} wants to teleport you to them. Type /tpaccept within {60} seconds (or /nse tpaccept if another mod owns that command). After accepting, you will be teleported in {10} seconds.", EnumChatType.Notification);
		}
	}

	public void HandleTpAccept(IServerPlayer target, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (IsJailed(target))
		{
			Reply(target, groupId, "You cannot accept a teleport request while jailed.", success: false);
			return;
		}
		if (!consentIncomingByTargetUid.TryGetValue(target.PlayerUID, out PendingConsentTeleportRequest value))
		{
			Reply(target, groupId, "You do not have a pending Suite teleport request to accept.", success: false);
			return;
		}
		if (!((value.Kind == "t2p") ? playerCommandConfig.T2pEnabled : playerCommandConfig.T2mEnabled))
		{
			RemoveConsentRequest(value);
			Reply(target, groupId, "The Server Essentials /" + value.Kind + " command is disabled.", success: false);
			return;
		}
		long num = NowMs();
		if (value.Accepted)
		{
			int value2 = Math.Max(0, (int)Math.Ceiling((double)(value.DueUtcMs - num) / 1000.0));
			Reply(target, groupId, $"That /{value.Kind} request is already accepted. Teleport completes in {value2} second(s).", success: false);
			return;
		}
		if (num >= value.ExpiresUtcMs)
		{
			RemoveConsentRequest(value);
			Reply(target, groupId, "That /" + value.Kind + " request has expired.", success: false);
			IServerPlayer serverPlayer = OnlineByUid(value.RequesterUid);
			if (serverPlayer != null)
			{
				Reply(serverPlayer, GlobalConstants.GeneralChatGroup, $"Your /{value.Kind} request to {target.PlayerName} expired.", success: false);
			}
			return;
		}
		IServerPlayer serverPlayer2 = OnlineByUid(value.RequesterUid);
		if (serverPlayer2 == null)
		{
			RemoveConsentRequest(value);
			Reply(target, groupId, "The player who sent that teleport request is no longer online.", success: false);
			return;
		}
		if (IsJailed(serverPlayer2))
		{
			RemoveConsentRequest(value);
			Reply(target, groupId, serverPlayer2.PlayerName + "'s /" + value.Kind + " request was cancelled because they are jailed.", success: false);
			return;
		}
		string text = ((value.Kind == "t2p") ? value.RequesterUid : value.TargetUid);
		if (pendingByUid.ContainsKey(text) || rtpSearching.Contains(text))
		{
			Reply(target, groupId, "The player who would be teleported already has another Suite teleport in progress.", success: false);
			return;
		}
		int defaultCooldownSeconds = ((value.Kind == "t2p") ? playerCommandConfig.T2pCooldownSeconds : playerCommandConfig.T2mCooldownSeconds);
		if (!TryResolveTeleportPolicy(serverPlayer2, value.Kind, defaultCooldownSeconds, GlobalConstants.GeneralChatGroup, out var cooldownSeconds, requireAdminWhenUnconfigured: true))
		{
			RemoveConsentRequest(value);
			Reply(target, groupId, serverPlayer2.PlayerName + "'s /" + value.Kind + " request is no longer permitted.", success: false);
			return;
		}
		if (!CheckCooldown(serverPlayer2, value.Kind, cooldownSeconds, out var remainingSeconds))
		{
			RemoveConsentRequest(value);
			Reply(target, groupId, serverPlayer2.PlayerName + "'s /" + value.Kind + " request can no longer be completed because their cooldown is active.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, $"Your /{value.Kind} cooldown has {remainingSeconds} second(s) remaining, so the request was cancelled.", success: false);
			return;
		}
		value.Accepted = true;
		value.DueUtcMs = num + 10000;
		if (value.Kind == "t2p")
		{
			Reply(target, groupId, $"Accepted {serverPlayer2.PlayerName}'s teleport request. They will be teleported to you in {10} seconds.", success: true);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, $"{target.PlayerName} accepted your /t2p request. You will be teleported to them in {10} seconds.", success: true);
		}
		else
		{
			Reply(target, groupId, $"Accepted {serverPlayer2.PlayerName}'s teleport request. Teleporting to them in {10} seconds.", success: true);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, $"{target.PlayerName} accepted your /t2m request. They will be teleported to you in {10} seconds.", success: true);
		}
	}

	public void HandleRtp(IServerPlayer player, int groupId, CmdArgs args)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (IsJailed(player))
		{
			Reply(player, groupId, "You cannot use /rtp while jailed.", success: false);
		}
		else if (!playerCommandConfig.RtpEnabled)
		{
			Reply(player, groupId, "Random teleport is disabled by Server Essentials.", success: false);
		}
		else if (pendingByUid.ContainsKey(player.PlayerUID) || rtpSearching.Contains(player.PlayerUID) || HasAcceptedConsentPlayer(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a teleport in progress.", success: false);
		}
		else
		{
			if (!TryResolveTeleportPolicy(player, "rtp", playerCommandConfig.RtpCooldownSeconds, groupId, out var cooldownSeconds))
			{
				return;
			}
			int count;
			if (!CheckCooldown(player, "rtp", cooldownSeconds, out var remainingSeconds))
			{
				Reply(player, groupId, $"You can use /rtp again in {remainingSeconds} second(s).", success: false);
			}
			else if (playerCommandConfig.RtpCostEnabled && !HasItemCost(player, playerCommandConfig.RtpCostItemCode, playerCommandConfig.RtpCostQuantity, out count))
			{
				Reply(player, groupId, $"Random teleport requires {playerCommandConfig.RtpCostQuantity} × {playerCommandConfig.RtpCostItemCode}. You currently have {count}.", success: false);
			}
			else
			{
				EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
				if (pos == null)
				{
					Reply(player, groupId, "Your current position could not be read safely.", success: false);
					return;
				}
				if (pos.Dimension != 0)
				{
					Reply(player, groupId, "Random teleport currently supports the overworld dimension only.", success: false);
					return;
				}
				rtpSearching.Add(player.PlayerUID);
				Reply(player, groupId, "Searching for a safe random teleport location...", success: true);
				TryFindRtpDestination(player.PlayerUID, groupId, 1);
			}
		}
	}

	public int GetHomeLimit(IServerPlayer player)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (playerCommandConfig.HomePlayerLimits.TryGetValue(player.PlayerUID ?? "", out var value))
		{
			return Math.Max(0, value);
		}
		if (playerCommandConfig.HomePlayerLimits.TryGetValue(player.PlayerName ?? "", out value))
		{
			return Math.Max(0, value);
		}
		string text = player.ServerData?.RoleCode ?? player.Role?.Code ?? "";
		if (!string.IsNullOrWhiteSpace(text) && playerCommandConfig.HomeRoleLimits.TryGetValue(text, out var value2))
		{
			return Math.Max(0, value2);
		}
		string text2 = player.Role?.Name ?? "";
		if (!string.IsNullOrWhiteSpace(text2) && playerCommandConfig.HomeRoleLimits.TryGetValue(text2, out value2))
		{
			return Math.Max(0, value2);
		}
		return Math.Max(0, playerCommandConfig.HomeGlobalLimit);
	}

	private void TryFindRtpDestination(string playerUid, int groupId, int attempt)
	{
		if (!rtpSearching.Contains(playerUid))
		{
			return;
		}
		IServerPlayer serverPlayer = OnlineByUid(playerUid);
		if (serverPlayer == null)
		{
			rtpSearching.Remove(playerUid);
			return;
		}
		if (IsJailed(serverPlayer))
		{
			rtpSearching.Remove(playerUid);
			Reply(serverPlayer, groupId, "Random teleport cancelled because you are jailed.", success: false);
			return;
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!playerCommandConfig.RtpEnabled)
		{
			rtpSearching.Remove(playerUid);
			Reply(serverPlayer, groupId, "Random teleport was disabled while the search was running.", success: false);
			return;
		}
		if (attempt > playerCommandConfig.RtpMaxSearchAttempts)
		{
			rtpSearching.Remove(playerUid);
			Reply(serverPlayer, groupId, $"No safe random teleport location was found after {playerCommandConfig.RtpMaxSearchAttempts} attempts. Nothing was consumed.", success: false);
			return;
		}
		EntityPos entityPos = ((!playerCommandConfig.RtpCenterOnPlayerPosition) ? api.World.DefaultSpawnPosition?.Copy() : EntityRuntimeCompat.GetPos(serverPlayer.Entity)?.Copy());
		if (entityPos == null)
		{
			rtpSearching.Remove(playerUid);
			Reply(serverPlayer, groupId, playerCommandConfig.RtpCenterOnPlayerPosition ? "Random teleport could not read your current position for the search center." : "Random teleport could not determine the server spawn position for the search center.", success: false);
			return;
		}
		double num = Math.Max(0, playerCommandConfig.RtpMinRadius);
		double num2 = Math.Max(num, playerCommandConfig.RtpMaxRadius);
		double num3 = Math.Sqrt(num * num + api.World.Rand.NextDouble() * (num2 * num2 - num * num));
		double num4 = api.World.Rand.NextDouble() * Math.PI * 2.0;
		int x = (int)Math.Round(entityPos.X + Math.Cos(num4) * num3);
		int z = (int)Math.Round(entityPos.Z + Math.Sin(num4) * num3);
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		if (x < 1 || z < 1 || x >= blockAccessor.MapSizeX - 1 || z >= blockAccessor.MapSizeZ - 1)
		{
			TryFindRtpDestination(playerUid, groupId, attempt + 1);
			return;
		}
		int chunkX = (int)Math.Floor((double)x / 32.0);
		int chunkZ = (int)Math.Floor((double)z / 32.0);
		try
		{
			if (api.WorldManager.GetChunk(chunkX, 0, chunkZ) != null)
			{
				ResolveRtpCandidate(playerUid, groupId, attempt, x, z);
				return;
			}
			api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
			{
				KeepLoaded = false,
				OnLoaded = () =>
				{
					api.Event.EnqueueMainThreadTask(() =>
					{
						ResolveRtpCandidate(playerUid, groupId, attempt, x, z);
					}, "nse-rtp-resolve");
				}
			});
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/RTP] Failed loading candidate chunk for {0}: {1}", serverPlayer.PlayerName, ex.Message);
			TryFindRtpDestination(playerUid, groupId, attempt + 1);
		}
	}

	private void ResolveRtpCandidate(string playerUid, int groupId, int attempt, int x, int z)
	{
		IServerPlayer serverPlayer = OnlineByUid(playerUid);
		if (serverPlayer == null || !rtpSearching.Contains(playerUid))
		{
			return;
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!playerCommandConfig.RtpEnabled)
		{
			rtpSearching.Remove(playerUid);
			Reply(serverPlayer, groupId, "Random teleport was disabled while the search was running.", success: false);
			return;
		}
		PlayerTeleportPosition playerTeleportPosition = FindSafeRtpPosition(x, z, playerCommandConfig.RtpAvoidWaterAndLava);
		if (playerTeleportPosition == null)
		{
			TryFindRtpDestination(playerUid, groupId, attempt + 1);
			return;
		}
		if (!TryResolveTeleportPolicy(serverPlayer, "rtp", playerCommandConfig.RtpCooldownSeconds, groupId, out var cooldownSeconds))
		{
			rtpSearching.Remove(playerUid);
			return;
		}
		rtpSearching.Remove(playerUid);
		BeginTeleport(serverPlayer, groupId, new PendingTeleport
		{
			PlayerUid = serverPlayer.PlayerUID,
			Kind = "rtp",
			Label = "random location",
			Destination = playerTeleportPosition,
			CancelOnMove = playerCommandConfig.RtpCancelOnMove,
			DueUtcMs = NowMs() + (long)playerCommandConfig.RtpWarmupSeconds * 1000L,
			CostEnabled = playerCommandConfig.RtpCostEnabled,
			CostItemCode = playerCommandConfig.RtpCostItemCode,
			CostQuantity = playerCommandConfig.RtpCostQuantity,
			CooldownKey = "rtp",
			CooldownSeconds = cooldownSeconds
		}, playerCommandConfig.RtpWarmupSeconds);
	}

	private PlayerTeleportPosition? FindSafeRtpPosition(int x, int z, bool avoidLiquids)
	{
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		int num = Math.Max(3, blockAccessor.MapSizeY - 3);
		int num2 = Math.Clamp(blockAccessor.GetRainMapHeightAt(x, z), 1, num - 1);
		int num3 = Math.Clamp(num2 + 1, 1, num);
		for (int i = 0; i <= 24; i++)
		{
			int num4 = num3 + i;
			if (num4 <= num && IsSafeStandingPosition(x, num4, z, avoidLiquids))
			{
				return new PlayerTeleportPosition
				{
					X = (double)x + 0.5,
					Y = num4,
					Z = (double)z + 0.5,
					Dimension = 0
				};
			}
			if (i != 0)
			{
				int num5 = num3 - i;
				if (num5 >= 1 && IsSafeStandingPosition(x, num5, z, avoidLiquids))
				{
					return new PlayerTeleportPosition
					{
						X = (double)x + 0.5,
						Y = num5,
						Z = (double)z + 0.5,
						Dimension = 0
					};
				}
			}
		}
		return null;
	}

	private bool IsSafeStandingPosition(int x, int y, int z, bool avoidLiquids)
	{
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		BlockPos pos = new BlockPos(x, y, z, 0);
		BlockPos pos2 = new BlockPos(x, y + 1, z, 0);
		BlockPos pos3 = new BlockPos(x, y - 1, z, 0);
		if (blockAccessor.IsNotTraversable(pos) || blockAccessor.IsNotTraversable(pos2) || !blockAccessor.IsNotTraversable(pos3))
		{
			return false;
		}
		if (!avoidLiquids)
		{
			return true;
		}
		Block block = blockAccessor.GetBlock(pos);
		Block block2 = blockAccessor.GetBlock(pos2);
		Block block3 = blockAccessor.GetBlock(pos3);
		return !IsUnsafeLiquid(block) && !IsUnsafeLiquid(block2) && !IsUnsafeLiquid(block3);
	}

	private static bool IsUnsafeLiquid(Block? block)
	{
		return block != null && (block.ForFluidsLayer || block.BlockMaterial == EnumBlockMaterial.Lava);
	}

	private void BeginTeleport(IServerPlayer player, int groupId, PendingTeleport pending, int warmupSeconds)
	{
		if (pendingByUid.ContainsKey(player.PlayerUID) || HasAcceptedConsentPlayer(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a teleport in progress.", success: false);
			return;
		}
		EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
		if (pos == null)
		{
			Reply(player, groupId, "Your current position could not be read safely.", success: false);
			return;
		}
		pending = new PendingTeleport
		{
			PlayerUid = pending.PlayerUid,
			Kind = pending.Kind,
			Label = pending.Label,
			DueUtcMs = pending.DueUtcMs,
			StartX = pos.X,
			StartY = pos.Y,
			StartZ = pos.Z,
			CancelOnMove = pending.CancelOnMove,
			Destination = pending.Destination,
			CostEnabled = pending.CostEnabled,
			CostItemCode = pending.CostItemCode,
			CostQuantity = pending.CostQuantity,
			CooldownKey = pending.CooldownKey,
			CooldownSeconds = pending.CooldownSeconds,
			SwapBackAfterTeleport = pending.SwapBackAfterTeleport
		};
		if (warmupSeconds <= 0)
		{
			CompleteTeleport(player, pending, groupId);
			return;
		}
		pendingByUid[player.PlayerUID] = pending;
		string value = (pending.CostEnabled ? $" Cost: {pending.CostQuantity} × {pending.CostItemCode}." : "");
		string value2 = (pending.CancelOnMove ? " Do not move." : "");
		Reply(player, groupId, $"Teleporting to {pending.Label} in {warmupSeconds} second(s).{value2}{value}", success: true);
	}

	private void Tick()
	{
		long maintenanceNow = NowMs();
		if (dirty && maintenanceNow - lastFlushUtcMs >= 5000)
		{
			Save();
		}
		if (maintenanceNow - lastCooldownPruneUtcMs >= 60000)
		{
			lastCooldownPruneUtcMs = maintenanceNow;
			foreach (string item in (from x in cooldownUtc
				where maintenanceNow - x.Value > 86400000
				select x.Key).ToList())
			{
				cooldownUtc.Remove(item);
			}
		}
		if (pendingByUid.Count == 0 && consentIncomingByTargetUid.Count == 0)
		{
			return;
		}
		long num = NowMs();
		PendingTeleport[] array = pendingByUid.Values.ToArray();
		foreach (PendingTeleport pendingTeleport in array)
		{
			IServerPlayer serverPlayer = OnlineByUid(pendingTeleport.PlayerUid);
			if (serverPlayer == null)
			{
				pendingByUid.Remove(pendingTeleport.PlayerUid);
				continue;
			}
			if (IsJailed(serverPlayer))
			{
				pendingByUid.Remove(pendingTeleport.PlayerUid);
				Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because you were jailed.", success: false);
				continue;
			}
			if (pendingTeleport.CancelOnMove)
			{
				EntityPos pos = EntityRuntimeCompat.GetPos(serverPlayer.Entity);
				if (pos == null)
				{
					pendingByUid.Remove(pendingTeleport.PlayerUid);
					Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because your position could not be read safely.", success: false);
					continue;
				}
				double num3 = pos.X - pendingTeleport.StartX;
				double num4 = pos.Y - pendingTeleport.StartY;
				double num5 = pos.Z - pendingTeleport.StartZ;
				if (num3 * num3 + num4 * num4 + num5 * num5 > 0.16)
				{
					pendingByUid.Remove(pendingTeleport.PlayerUid);
					Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because you moved.", success: false);
					continue;
				}
			}
			if (num >= pendingTeleport.DueUtcMs)
			{
				pendingByUid.Remove(pendingTeleport.PlayerUid);
				CompleteTeleport(serverPlayer, pendingTeleport, GlobalConstants.GeneralChatGroup);
			}
		}
		PendingConsentTeleportRequest[] array2 = consentIncomingByTargetUid.Values.Distinct().ToArray();
		foreach (PendingConsentTeleportRequest pendingConsentTeleportRequest in array2)
		{
			if (!pendingConsentTeleportRequest.Accepted)
			{
				if (num >= pendingConsentTeleportRequest.ExpiresUtcMs)
				{
					RemoveConsentRequest(pendingConsentTeleportRequest);
					IServerPlayer serverPlayer2 = OnlineByUid(pendingConsentTeleportRequest.RequesterUid);
					IServerPlayer serverPlayer3 = OnlineByUid(pendingConsentTeleportRequest.TargetUid);
					if (serverPlayer2 != null)
					{
						Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, $"Your /{pendingConsentTeleportRequest.Kind} request to {pendingConsentTeleportRequest.TargetName} expired.", success: false);
					}
					if (serverPlayer3 != null)
					{
						Reply(serverPlayer3, GlobalConstants.GeneralChatGroup, $"The /{pendingConsentTeleportRequest.Kind} request from {pendingConsentTeleportRequest.RequesterName} expired.", success: false);
					}
				}
			}
			else if (num >= pendingConsentTeleportRequest.DueUtcMs)
			{
				RemoveConsentRequest(pendingConsentTeleportRequest);
				CompleteAcceptedConsentTeleport(pendingConsentTeleportRequest);
			}
		}
	}

	private void CompleteAcceptedConsentTeleport(PendingConsentTeleportRequest request)
	{
		IServerPlayer serverPlayer = OnlineByUid(request.RequesterUid);
		IServerPlayer serverPlayer2 = OnlineByUid(request.TargetUid);
		if (serverPlayer == null)
		{
			if (serverPlayer2 != null)
			{
				Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport cancelled because the requesting player went offline.", success: false);
			}
			return;
		}
		if (serverPlayer2 == null)
		{
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because " + request.TargetName + " went offline.", success: false);
			return;
		}
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (!((request.Kind == "t2p") ? playerCommandConfig.T2pEnabled : playerCommandConfig.T2mEnabled))
		{
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because /" + request.Kind + " was disabled.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport cancelled because /" + request.Kind + " was disabled.", success: false);
			return;
		}
		if (IsJailed(serverPlayer) || IsJailed(serverPlayer2))
		{
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because one of the players is jailed.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport cancelled because one of the players is jailed.", success: false);
			return;
		}
		IServerPlayer serverPlayer3 = ((request.Kind == "t2p") ? serverPlayer : serverPlayer2);
		IServerPlayer serverPlayer4 = ((request.Kind == "t2p") ? serverPlayer2 : serverPlayer);
		if (pendingByUid.ContainsKey(serverPlayer3.PlayerUID) || rtpSearching.Contains(serverPlayer3.PlayerUID))
		{
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because " + serverPlayer3.PlayerName + " has another Suite teleport in progress.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport cancelled because another Suite teleport is in progress.", success: false);
			return;
		}
		int defaultCooldownSeconds = ((request.Kind == "t2p") ? playerCommandConfig.T2pCooldownSeconds : playerCommandConfig.T2mCooldownSeconds);
		if (!TryResolveTeleportPolicy(serverPlayer, request.Kind, defaultCooldownSeconds, GlobalConstants.GeneralChatGroup, out var cooldownSeconds, requireAdminWhenUnconfigured: true))
		{
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, serverPlayer.PlayerName + "'s /" + request.Kind + " request is no longer permitted.", success: false);
			return;
		}
		if (!CheckCooldown(serverPlayer, request.Kind, cooldownSeconds, out var remainingSeconds))
		{
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, $"Teleport cancelled because your /{request.Kind} cooldown has {remainingSeconds} second(s) remaining.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport cancelled because the requester's /" + request.Kind + " cooldown is active.", success: false);
			return;
		}
		EntityPos entityPos = EntityRuntimeCompat.GetPos(serverPlayer4.Entity)?.Copy();
		EntityPos entityPos2 = EntityRuntimeCompat.GetPos(serverPlayer3.Entity)?.Copy();
		if (entityPos == null || entityPos2 == null)
		{
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport cancelled because a player position could not be read safely.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport cancelled because a player position could not be read safely.", success: false);
			return;
		}
		try
		{
			PacketGuardTrustedState.Mark(serverPlayer3, request.Kind, 5000);
			serverPlayer3.Entity.TeleportTo(entityPos);
			RecordTeleportOrigin(serverPlayer3, entityPos2, request.Kind);
			if (cooldownSeconds > 0)
			{
				cooldownUtc[CooldownKey(serverPlayer.PlayerUID, request.Kind)] = NowMs();
			}
			if (request.Kind == "t2p")
			{
				Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleported to " + serverPlayer2.PlayerName + " after they accepted your request.", success: true);
				Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, serverPlayer.PlayerName + " was teleported to your position after you accepted the request.", success: true);
			}
			else
			{
				Reply(serverPlayer, GlobalConstants.GeneralChatGroup, serverPlayer2.PlayerName + " was teleported to your position after accepting the request.", success: true);
				Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleported to " + serverPlayer.PlayerName + ".", success: true);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/PlayerTeleports] accepted {0} teleport failed for {1} -> {2}: {3}", request.Kind, serverPlayer.PlayerName, serverPlayer2.PlayerName, ex);
			Reply(serverPlayer, GlobalConstants.GeneralChatGroup, "Teleport failed.", success: false);
			Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, "Teleport failed.", success: false);
		}
	}

	private bool HasAcceptedConsentPlayer(string playerUid)
	{
		if (string.IsNullOrWhiteSpace(playerUid))
		{
			return false;
		}
		if (consentIncomingByTargetUid.TryGetValue(playerUid, out PendingConsentTeleportRequest value) && value.Accepted)
		{
			return true;
		}
		PendingConsentTeleportRequest value2;
		return consentOutgoingByRequesterUid.TryGetValue(playerUid, out value2) && value2.Accepted;
	}

	private void RemoveConsentRequest(PendingConsentTeleportRequest request)
	{
		if (request != null)
		{
			if (consentIncomingByTargetUid.TryGetValue(request.TargetUid, out PendingConsentTeleportRequest value) && value == request)
			{
				consentIncomingByTargetUid.Remove(request.TargetUid);
			}
			if (consentOutgoingByRequesterUid.TryGetValue(request.RequesterUid, out PendingConsentTeleportRequest value2) && value2 == request)
			{
				consentOutgoingByRequesterUid.Remove(request.RequesterUid);
			}
		}
	}

	private void CompleteTeleport(IServerPlayer player, PendingTeleport pending, int groupId)
	{
		if (IsJailed(player))
		{
			Reply(player, groupId, "Teleport cancelled because you are jailed.", success: false);
			return;
		}
		EntityPos entityPos = EntityRuntimeCompat.GetPos(player.Entity)?.Copy();
		if (entityPos == null)
		{
			Reply(player, groupId, "Teleport cancelled because your current position could not be read. Nothing was consumed.", success: false);
			return;
		}
		if (pending.CostEnabled && !HasItemCost(player, pending.CostItemCode, pending.CostQuantity, out var count))
		{
			Reply(player, groupId, $"Teleport requires {pending.CostQuantity} × {pending.CostItemCode}. You currently have {count}. Nothing was consumed.", success: false);
			return;
		}
		ItemStack refund = null;
		if (pending.CostEnabled && !TryRemoveItemCost(player, pending.CostItemCode, pending.CostQuantity, out refund))
		{
			Reply(player, groupId, "Your inventory changed before teleport completed, so nothing was consumed.", success: false);
			return;
		}
		try
		{
			PacketGuardTrustedState.Mark(player, pending.Kind, 5000);
			player.Entity.TeleportTo(pending.Destination.ToEntityPos());
			if (pending.SwapBackAfterTeleport)
			{
				SetBack(player, PlayerTeleportPosition.From(entityPos), "back");
			}
			else
			{
				RecordTeleportOrigin(player, entityPos, pending.Kind);
			}
			if (pending.CooldownSeconds > 0)
			{
				cooldownUtc[CooldownKey(player.PlayerUID, pending.CooldownKey)] = NowMs();
			}
			Reply(player, groupId, pending.CostEnabled ? $"Teleported to {pending.Label}. Consumed {pending.CostQuantity} × {pending.CostItemCode}." : ("Teleported to " + pending.Label + "."), success: true);
		}
		catch (Exception ex)
		{
			if (refund != null)
			{
				try
				{
					player.Entity.TryGiveItemStack(refund);
				}
				catch
				{
				}
			}
			api.Logger.Error("[ServerEssentials/PlayerTeleports] {0} teleport failed for {1}: {2}", pending.Kind, player.PlayerName, ex);
			Reply(player, groupId, "Teleport failed. Any consumed item cost was refunded where inventory space allowed.", success: false);
		}
	}

	private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
	{
		PlayerCommandConfig playerCommandConfig = getConfig();
		if (playerCommandConfig.BackEnabled && playerCommandConfig.BackTrackDeaths)
		{
			EntityPos pos = EntityRuntimeCompat.GetPos(player.Entity);
			if (pos != null)
			{
				SetBack(player, PlayerTeleportPosition.From(pos), "last death");
			}
		}
	}

	private void SetBack(IServerPlayer player, PlayerTeleportPosition position, string reason)
	{
		if (storageReady && !disposed)
		{
			PlayerTeleportProfile playerTeleportProfile = Profile(player);
			playerTeleportProfile.Back = Clone(position);
			playerTeleportProfile.BackReason = (string.IsNullOrWhiteSpace(reason) ? "previous location" : reason.Trim());
			playerTeleportProfile.BackUpdatedAtUnixMilliseconds = NowMs();
			playerTeleportProfile.LastKnownName = player.PlayerName;
			dirty = true;
		}
	}

	private bool IsJailed(IServerPlayer player)
	{
		return player != null && (isJailed?.Invoke(player.PlayerUID) ?? false);
	}

	private static string RoleCode(IServerPlayer player)
	{
		return (player.ServerData?.RoleCode ?? player.Role?.Code ?? "").Trim();
	}

	private bool TryResolveTeleportPolicy(IServerPlayer player, string command, int defaultCooldownSeconds, int groupId, out int cooldownSeconds, bool requireAdminWhenUnconfigured = false)
	{
		TeleportRolePolicyResolution teleportRolePolicyResolution = getConfig().ResolveTeleportRolePolicy(command, RoleCode(player), defaultCooldownSeconds);
		cooldownSeconds = teleportRolePolicyResolution.CooldownSeconds;
		if (requireAdminWhenUnconfigured && !teleportRolePolicyResolution.Overridden && !player.HasPrivilege(Privilege.root) && !player.HasPrivilege(Privilege.controlserver))
		{
			string value = (string.IsNullOrWhiteSpace(teleportRolePolicyResolution.RoleCode) ? "your role" : ("role '" + teleportRolePolicyResolution.RoleCode + "'"));
			Reply(player, groupId, $"/{command} is not enabled for {value}. An administrator can grant it in Player Commands → Teleport Role Access & Cooldowns.", success: false);
			return false;
		}
		if (teleportRolePolicyResolution.Allowed)
		{
			return true;
		}
		string value2 = (string.IsNullOrWhiteSpace(teleportRolePolicyResolution.RoleCode) ? "your role" : ("role '" + teleportRolePolicyResolution.RoleCode + "'"));
		Reply(player, groupId, $"/{command} is not available to {value2}.", success: false);
		return false;
	}

	private bool CheckCooldown(IServerPlayer player, string key, int cooldownSeconds, out int remainingSeconds)
	{
		remainingSeconds = 0;
		if (cooldownSeconds <= 0)
		{
			return true;
		}
		string key2 = CooldownKey(player.PlayerUID, key);
		if (!cooldownUtc.TryGetValue(key2, out var value))
		{
			return true;
		}
		long num = value + (long)cooldownSeconds * 1000L - NowMs();
		if (num <= 0)
		{
			return true;
		}
		remainingSeconds = (int)Math.Ceiling((double)num / 1000.0);
		return false;
	}

	private static string CooldownKey(string uid, string key)
	{
		return uid + ":" + key;
	}

	private PlayerTeleportProfile Profile(IServerPlayer player)
	{
		if (!profiles.TryGetValue(player.PlayerUID, out PlayerTeleportProfile value))
		{
			value = new PlayerTeleportProfile
			{
				PlayerUid = player.PlayerUID,
				LastKnownName = player.PlayerName
			};
			profiles[player.PlayerUID] = value;
		}
		PlayerTeleportProfile playerTeleportProfile = value;
		if (playerTeleportProfile.Homes == null)
		{
			List<PlayerHomeDefinition> list = (playerTeleportProfile.Homes = new List<PlayerHomeDefinition>());
		}
		value.LastKnownName = player.PlayerName;
		return value;
	}

	private PlayerHomeDefinition? ResolveHome(PlayerTeleportProfile profile, string requested, out string error)
	{
		error = "";
		if (!string.IsNullOrWhiteSpace(requested))
		{
			string name = NormalizeHomeName(requested);
			PlayerHomeDefinition playerHomeDefinition = profile.Homes.FirstOrDefault((PlayerHomeDefinition h) => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
			if (playerHomeDefinition != null)
			{
				return playerHomeDefinition;
			}
			error = "Home '" + requested + "' was not found. Use /homes to list your homes.";
			return null;
		}
		PlayerHomeDefinition playerHomeDefinition2 = profile.Homes.FirstOrDefault((PlayerHomeDefinition h) => h.Name.Equals("home", StringComparison.OrdinalIgnoreCase));
		if (playerHomeDefinition2 != null)
		{
			return playerHomeDefinition2;
		}
		if (profile.Homes.Count == 1)
		{
			return profile.Homes[0];
		}
		if (profile.Homes.Count == 0)
		{
			error = "You have no homes. Use /sethome [name].";
		}
		else
		{
			error = "You have multiple homes. Use /home <name>.";
		}
		return null;
	}

	public static string NormalizeHomeName(string? value)
	{
		string text = (value ?? "").Trim().ToLowerInvariant();
		if (text.Length == 0)
		{
			return "home";
		}
		if (text.Length > 32)
		{
			return "";
		}
		return text.All((char c) =>
		{
			bool flag = char.IsLetterOrDigit(c);
			bool flag2 = flag;
			if (!flag2)
			{
				bool flag3 = ((c == '-' || c == '_') ? true : false);
				flag2 = flag3;
			}
			return flag2;
		}) ? text : "";
	}

	private static PlayerTeleportPosition Clone(PlayerTeleportPosition p)
	{
		return new PlayerTeleportPosition
		{
			X = p.X,
			Y = p.Y,
			Z = p.Z,
			Dimension = p.Dimension,
			Yaw = p.Yaw,
			Pitch = p.Pitch
		};
	}

	private IServerPlayer? OnlineByUid(string uid)
	{
		return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerUID, uid, StringComparison.OrdinalIgnoreCase));
	}

	private IServerPlayer? OnlineByName(string name)
	{
		return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));
	}

	private bool HasItemCost(IServerPlayer player, string itemCode, int quantity, out int count)
	{
		count = 0;
		if (string.IsNullOrWhiteSpace(itemCode) || quantity <= 0)
		{
			return true;
		}
		foreach (InventoryBase item in player.InventoryManager.InventoriesOrdered)
		{
			if (item == null)
			{
				continue;
			}
			for (int i = 0; i < item.Count; i++)
			{
				ItemStack itemStack = item[i]?.Itemstack;
				if (!(itemStack?.Collectible?.Code == null) && itemStack.Collectible.Code.ToString().Equals(itemCode, StringComparison.OrdinalIgnoreCase))
				{
					count += Math.Max(0, itemStack.StackSize);
					if (count >= quantity)
					{
						return true;
					}
				}
			}
		}
		return false;
	}

	private bool TryRemoveItemCost(IServerPlayer player, string itemCode, int quantity, out ItemStack? refund)
	{
		refund = null;
		if (!HasItemCost(player, itemCode, quantity, out var _))
		{
			return false;
		}
		int num = quantity;
		CollectibleObject collectibleObject = null;
		foreach (InventoryBase item in player.InventoryManager.InventoriesOrdered)
		{
			if (item == null)
			{
				continue;
			}
			for (int i = 0; i < item.Count; i++)
			{
				if (num <= 0)
				{
					break;
				}
				ItemSlot itemSlot = item[i];
				ItemStack itemStack = itemSlot?.Itemstack;
				if (itemSlot != null && !(itemStack?.Collectible?.Code == null) && itemStack.Collectible.Code.ToString().Equals(itemCode, StringComparison.OrdinalIgnoreCase))
				{
					if (collectibleObject == null)
					{
						collectibleObject = itemStack.Collectible;
					}
					int num2 = Math.Min(num, itemStack.StackSize);
					itemStack.StackSize -= num2;
					num -= num2;
					if (itemStack.StackSize <= 0)
					{
						itemSlot.Itemstack = null;
					}
					itemSlot.MarkDirty();
				}
			}
			if (num > 0)
			{
				continue;
			}
			break;
		}
		if (num > 0 || collectibleObject == null)
		{
			return false;
		}
		refund = new ItemStack(collectibleObject, quantity);
		return true;
	}

	private void Load()
	{
		try
		{
			byte[] data = api.WorldManager.SaveGame.GetData("newfiesserveressentials:player-teleports-v1");
			if (data != null && data.Length > 33554432)
			{
				throw new InvalidOperationException("Saved teleport data exceeds the 32 MiB safety limit.");
			}
			PlayerTeleportDatabase playerTeleportDatabase = ((data == null || data.Length == 0) ? new PlayerTeleportDatabase() : (JsonSerializer.Deserialize<PlayerTeleportDatabase>(data) ?? throw new InvalidOperationException("Saved teleport data is null.")));
			if (playerTeleportDatabase.SchemaVersion != 1 || playerTeleportDatabase.Players == null)
			{
				throw new InvalidOperationException("Unsupported or invalid teleport data schema.");
			}
			Dictionary<string, PlayerTeleportProfile> dictionary = new Dictionary<string, PlayerTeleportProfile>(StringComparer.OrdinalIgnoreCase);
			foreach (PlayerTeleportProfile player in playerTeleportDatabase.Players)
			{
				if (player == null || string.IsNullOrWhiteSpace(player.PlayerUid) || dictionary.ContainsKey(player.PlayerUid))
				{
					throw new InvalidOperationException("Invalid or duplicate saved player profile.");
				}
				PlayerTeleportProfile playerTeleportProfile = player;
				if (playerTeleportProfile.Homes == null)
				{
					List<PlayerHomeDefinition> list = (playerTeleportProfile.Homes = new List<PlayerHomeDefinition>());
				}
				if (player.Homes.Any((PlayerHomeDefinition h) => h == null || h.Position == null || string.IsNullOrWhiteSpace(NormalizeHomeName(h.Name)) || !ValidStoredPosition(h.Position)) || (player.Back != null && !ValidStoredPosition(player.Back)))
				{
					throw new InvalidOperationException("Invalid saved home/back position.");
				}
				player.Homes = (from g in player.Homes.Where((PlayerHomeDefinition h) => h != null && h.Position != null && !string.IsNullOrWhiteSpace(NormalizeHomeName(h.Name))).GroupBy((PlayerHomeDefinition h) => NormalizeHomeName(h.Name), StringComparer.OrdinalIgnoreCase)
					select g.OrderByDescending((PlayerHomeDefinition h) => h.UpdatedAtUnixMilliseconds).First()).ToList();
				dictionary[player.PlayerUid] = player;
			}
			profiles.Clear();
			foreach (KeyValuePair<string, PlayerTeleportProfile> item in dictionary)
			{
				profiles[item.Key] = item.Value;
			}
			storageReady = true;
			StorageError = "";
			dirty = false;
		}
		catch (Exception ex)
		{
			storageReady = false;
			StorageError = ex.Message;
			api.Logger.Error("[ServerEssentials/PlayerTeleports] Failed to load saved homes/back positions. Writes are disabled; saved bytes and cached profiles were retained. Restore valid data and restart to recover. {0}", ex);
		}
	}

	private static bool ValidStoredPosition(PlayerTeleportPosition pos)
	{
		return double.IsFinite(pos.X) && double.IsFinite(pos.Y) && double.IsFinite(pos.Z) && float.IsFinite(pos.Yaw) && float.IsFinite(pos.Pitch);
	}

	private bool EnsureWritable(IServerPlayer player, int groupId)
	{
		if (storageReady && !disposed)
		{
			return true;
		}
		Reply(player, groupId, "Saved homes/back data is unavailable. Changes are disabled to protect existing data; ask an administrator to check the server log.", success: false);
		return false;
	}

	private void OnWorldSave()
	{
		if (dirty)
		{
			Save();
		}
	}

	private bool Save()
	{
		if (!storageReady || disposed)
		{
			return false;
		}
		dirty = true;
		lastFlushUtcMs = NowMs();
		try
		{
			PlayerTeleportDatabase value = new PlayerTeleportDatabase
			{
				Players = profiles.Values.OrderBy((PlayerTeleportProfile p) => p.LastKnownName, StringComparer.OrdinalIgnoreCase).ToList()
			};
			byte[] array = JsonSerializer.SerializeToUtf8Bytes(value);
			if (array.Length > 33554432)
			{
				throw new InvalidDataException("Home/back data exceeds the 32 MiB safety limit; the previous saved data was retained.");
			}
			api.WorldManager.SaveGame.StoreData("newfiesserveressentials:player-teleports-v1", array);
			dirty = false;
			StorageError = "";
			return true;
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/PlayerTeleports] Failed to save player teleport data: {0}", ex);
			StorageError = ex.Message;
			return false;
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		pendingByUid.Remove(player.PlayerUID);
		rtpSearching.Remove(player.PlayerUID);
		if (consentIncomingByTargetUid.TryGetValue(player.PlayerUID, out PendingConsentTeleportRequest value))
		{
			RemoveConsentRequest(value);
			IServerPlayer serverPlayer = OnlineByUid(value.RequesterUid);
			if (serverPlayer != null)
			{
				Reply(serverPlayer, GlobalConstants.GeneralChatGroup, $"Your /{value.Kind} request to {player.PlayerName} was cancelled because they disconnected.", success: false);
			}
		}
		if (consentOutgoingByRequesterUid.TryGetValue(player.PlayerUID, out PendingConsentTeleportRequest value2))
		{
			RemoveConsentRequest(value2);
			IServerPlayer serverPlayer2 = OnlineByUid(value2.TargetUid);
			if (serverPlayer2 != null)
			{
				Reply(serverPlayer2, GlobalConstants.GeneralChatGroup, $"The /{value2.Kind} request from {player.PlayerName} was cancelled because they disconnected.", success: false);
			}
		}
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}

	private static void Reply(IServerPlayer player, int groupId, string message, bool success)
	{
		player.SendMessage(groupId, message, (!success) ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}
		if (dirty)
		{
			Save();
		}
		disposed = true;
		api.Event.PlayerDeath -= OnPlayerDeath;
		api.Event.PlayerDisconnect -= OnPlayerDisconnect;
		api.Event.GameWorldSave -= OnWorldSave;
		if (tickListenerId > 0)
		{
			try
			{
				api.Event.UnregisterGameTickListener(tickListenerId);
			}
			catch
			{
			}
		}
		pendingByUid.Clear();
		consentIncomingByTargetUid.Clear();
		consentOutgoingByRequesterUid.Clear();
		rtpSearching.Clear();
		cooldownUtc.Clear();
	}
}
