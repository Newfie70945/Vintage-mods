using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BlockLogger.Admin;
using BlockLogger.Roles;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerCommandsService : IDisposable
{
	private readonly ICoreServerAPI api;

	private readonly Func<AdminService?> getAdmin;

	private readonly PlayerCommandConfig config;

	private readonly Dictionary<string, string> lastCorrespondentByUid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private const string StarterKitUsageSaveKey = "newfiesserveressentials:starterkit-usage-v1";

	private readonly HashSet<string> suiteOwnedRootCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> starterKitClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private StarterKitDefinition starterKit;

	private readonly PlayerTeleportService teleports;

	private readonly WarpService warps;

	private bool disposed;

	public PlayerCommandConfig Config => config;

	public WarpService Warps => warps;

	public PlayerTeleportService Teleports => teleports;

	private bool HasTh3Essentials => api.ModLoader.Mods.Any((Mod mod) => mod?.Info != null && string.Equals(mod.Info.ModID, "th3essentials", StringComparison.OrdinalIgnoreCase));

	private string StarterKitEffectiveProvider
	{
		get
		{
			string text = PlayerCommandConfig.NormalizeStarterKitProvider(config.StarterKitProvider);
			if (text == "NSE")
			{
				return "NSE";
			}
			if (text == "Th3Essentials")
			{
				return "Th3Essentials";
			}
			return HasTh3Essentials ? "Th3Essentials" : "NSE";
		}
	}

	private bool UsesNseStarterKit => StarterKitEffectiveProvider == "NSE";

	public PlayerCommandsService(ICoreServerAPI api, Func<AdminService?> getAdmin)
	{
		PlayerCommandsService playerCommandsService = this;
		this.api = api;
		this.getAdmin = getAdmin;
		config = PlayerCommandConfig.Load(api);
		starterKit = StarterKitDefinition.Load(api);
		teleports = new PlayerTeleportService(api, () => playerCommandsService.config, (string uid) => getAdmin()?.IsJailed(uid) ?? false);
		warps = new WarpService(api, () => playerCommandsService.config, (IServerPlayer player, EntityPos? origin, string reason) =>
		{
			playerCommandsService.teleports.RecordTeleportOrigin(player, origin, reason);
		}, (string uid) => getAdmin()?.IsJailed(uid) ?? false);
		LoadStarterKitUsage();
	}

	public void Start()
	{
		if (HasTh3Essentials)
		{
			api.Logger.Notification("[ServerEssentials/PlayerCommands] Th3Essentials detected. NSE will not register root /spawn, /msg, /r, /starterkit, /setstarterkit or starter-kit reset aliases. Th3Essentials keeps those roots. Warp aliases are registered only when free. NSE fallbacks remain available under /nse.");
		}
		else
		{
			SafeRegisterRoot("spawn", "Teleport to the global world spawn", "", HandleSpawn, Privilege.chat);
			SafeRegisterRoot("msg", "Send a private message to an online player", "<player> <message>", HandleMsg, Privilege.chat);
			SafeRegisterRoot("r", "Reply to your last private-message correspondent", "<message>", HandleReply, Privilege.chat);
			SafeRegisterRoot("starterkit", "Claim the configured starter kit once", "", HandleStarterKit, Privilege.chat);
			SafeRegisterRoot("setstarterkit", "Set the NSE starter kit from your hotbar", "", HandleSetStarterKit, Privilege.controlserver);
			SafeRegisterRoot("resetstarterkitusage", "Reset starter-kit usage for one online player", "<player>", HandleResetStarterKitUsage, Privilege.controlserver);
			SafeRegisterRoot("resetstarterkitusageall", "Reset starter-kit usage for all players", "", HandleResetStarterKitUsageAll, Privilege.controlserver);
		}
		SafeRegisterRoot("back", "Return to your last death or Suite teleport position", "", HandleBack, Privilege.chat);
		SafeRegisterRoot("home", "Teleport to one of your homes", "", HandleHome, Privilege.chat);
		SafeRegisterRoot("sethome", "Set or move one of your homes", "", HandleSetHome, Privilege.chat);
		SafeRegisterRoot("delhome", "Delete one of your homes", "", HandleDeleteHome, Privilege.chat);
		SafeRegisterRoot("homes", "List your homes and current home limit", "", HandleHomes, Privilege.chat);
		SafeRegisterRoot("t2p", "Teleport yourself to an online player", "<player>", HandleT2p, Privilege.chat);
		SafeRegisterRoot("t2m", "Request an online player teleport to you", "<player>", HandleT2m, Privilege.chat);
		SafeRegisterRoot("tpaccept", "Accept a pending /t2m teleport request", "", HandleTpAccept, Privilege.chat);
		SafeRegisterRoot("rtp", "Teleport to a safe random location", "", HandleRtp, Privilege.chat);
		SafeRegisterRoot("warp", "Teleport to a configured server warp", "<name>", HandleWarp, Privilege.chat);
		SafeRegisterRoot("warps", "List configured server warps", "", HandleWarps, Privilege.chat);
		SafeRegisterRoot("warpinfo", "Show details for a configured server warp", "<name>", HandleWarpInfo, Privilege.chat);
		SafeRegisterRoot("setwarp", "Create or move a server warp", "<name> [itemcode] [quantity]", HandleSetWarp, Privilege.controlserver);
		SafeRegisterRoot("delwarp", "Delete a server warp", "<name>", HandleDeleteWarp, Privilege.controlserver);
		SafeRegisterRoot("setwarpcost", "Set or clear a warp item cost", "<warp> <itemcode|none> [quantity]", HandleSetWarpCost, Privilege.controlserver);
		SafeRegisterRoot("setwarpcooldown", "Set a per-warp cooldown override", "<warp> <seconds|default>", HandleSetWarpCooldown, Privilege.controlserver);
		SafeRegisterRoot("setwarpwarmup", "Set a per-warp warm-up override", "<warp> <seconds|default>", HandleSetWarpWarmup, Privilege.controlserver);
		SafeRegisterRoot("setwarpmove", "Set movement cancellation for a warp", "<warp> <on|off|default>", HandleSetWarpMovement, Privilege.controlserver);
		SafeRegisterRoot("warpreload", "Reload warp definitions from disk", "", HandleReloadWarps, Privilege.controlserver);
		teleports.Start();
		warps.Start();
		api.Event.PlayerDisconnect += OnPlayerDisconnect;
		api.Logger.Notification("[ServerEssentials/PlayerCommands] Started. spawn={0}, msg={1}, reply={2}, starterkit={3}, starterKitProvider={4}, spawnCooldown={5}s.", config.SpawnEnabled, config.PrivateMessagesEnabled, config.ReplyEnabled, config.StarterKitEnabled, StarterKitEffectiveProvider, config.SpawnCooldownSeconds);
	}

	private bool IsIndividualCommandEnabled(string key)
	{
		bool value;
		return !config.CommandEnabled.TryGetValue(key, out value) | value;
	}

	private bool IsCommandEnabled(string key, bool masterEnabled)
	{
		return masterEnabled && IsIndividualCommandEnabled(key);
	}

	public PlayerCommandSaveResult SetCommandEnabled(string key, bool enabled)
	{
		key = (key ?? "").Trim().ToLowerInvariant();
		if (!PlayerCommandConfig.IsKnownCommandKey(key))
		{
			return new PlayerCommandSaveResult
			{
				Success = false,
				Message = "Unknown player command.",
				Snapshot = GetSnapshot()
			};
		}
		config.CommandEnabled[key] = enabled;
		config.Clamp();
		SuiteConfigStorage.Store(api, config, "NewfiesPlayerCommands.json");
		return new PlayerCommandSaveResult
		{
			Success = true,
			Message = $"/{((key == "reply") ? "r" : key)} is now {(enabled ? "enabled" : "disabled")} within its subsystem.",
			Snapshot = GetSnapshot()
		};
	}

	public PlayerCommandSnapshot GetSnapshot()
	{
		bool hasTh3Essentials = HasTh3Essentials;
		return new PlayerCommandSnapshot
		{
			Available = true,
			Th3EssentialsDetected = hasTh3Essentials,
			SpawnEnabled = config.SpawnEnabled,
			SpawnCooldownSeconds = config.SpawnCooldownSeconds,
			SpawnWarmupSeconds = config.SpawnWarmupSeconds,
			SpawnCancelOnMove = config.SpawnCancelOnMove,
			PrivateMessagesEnabled = config.PrivateMessagesEnabled,
			ReplyEnabled = config.ReplyEnabled,
			RespectSuiteMutes = config.RespectSuiteMutes,
			MaxPrivateMessageLength = config.MaxPrivateMessageLength,
			StarterKitEnabled = config.StarterKitEnabled,
			StarterKitProvider = PlayerCommandConfig.NormalizeStarterKitProvider(config.StarterKitProvider),
			StarterKitEffectiveProvider = StarterKitEffectiveProvider,
			StarterKitConfigured = (starterKit.Items.Count > 0),
			StarterKitItemCount = starterKit.Items.Count,
			StarterKitClaimedCount = starterKitClaims.Count,
			WarpsEnabled = config.WarpsEnabled,
			WarpCommandEnabled = config.WarpCommandEnabled,
			WarpDefaultCooldownSeconds = config.WarpDefaultCooldownSeconds,
			WarpDefaultWarmupSeconds = config.WarpDefaultWarmupSeconds,
			WarpDefaultCancelOnMove = config.WarpDefaultCancelOnMove,
			WarpCount = warps.GetSnapshot().Warps.Count,
			BackEnabled = config.BackEnabled,
			BackCooldownSeconds = config.BackCooldownSeconds,
			BackWarmupSeconds = config.BackWarmupSeconds,
			BackCancelOnMove = config.BackCancelOnMove,
			BackTrackDeaths = config.BackTrackDeaths,
			BackTrackTeleports = config.BackTrackTeleports,
			HomesEnabled = config.HomesEnabled,
			HomeGlobalLimit = config.HomeGlobalLimit,
			RolePolicyRevision = RoleManagementService.PolicyRevision(config),
			HomeRoleLimits = new Dictionary<string, int>(config.HomeRoleLimits, StringComparer.OrdinalIgnoreCase),
			HomePlayerLimits = new Dictionary<string, int>(config.HomePlayerLimits, StringComparer.OrdinalIgnoreCase),
			HomeCooldownSeconds = config.HomeCooldownSeconds,
			HomeWarmupSeconds = config.HomeWarmupSeconds,
			HomeCancelOnMove = config.HomeCancelOnMove,
			T2pEnabled = config.T2pEnabled,
			T2pCooldownSeconds = config.T2pCooldownSeconds,
			T2mEnabled = config.T2mEnabled,
			T2mCooldownSeconds = config.T2mCooldownSeconds,
			RtpEnabled = config.RtpEnabled,
			RtpMinRadius = config.RtpMinRadius,
			RtpMaxRadius = config.RtpMaxRadius,
			RtpCooldownSeconds = config.RtpCooldownSeconds,
			RtpWarmupSeconds = config.RtpWarmupSeconds,
			RtpCancelOnMove = config.RtpCancelOnMove,
			RtpCostEnabled = config.RtpCostEnabled,
			RtpCostItemCode = config.RtpCostItemCode,
			RtpCostQuantity = config.RtpCostQuantity,
			RtpMaxSearchAttempts = config.RtpMaxSearchAttempts,
			RtpAvoidWaterAndLava = config.RtpAvoidWaterAndLava,
			RtpCenterOnPlayerPosition = config.RtpCenterOnPlayerPosition,
			TeleportRolePolicies = config.TeleportRolePolicies.ToDictionary((KeyValuePair<string, Dictionary<string, TeleportRolePolicy>> x) => x.Key, (KeyValuePair<string, Dictionary<string, TeleportRolePolicy>> x) => new Dictionary<string, TeleportRolePolicy>(x.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
			AvailableRoles = (from r in (from r in api.Server.Config.Roles ?? new List<IPlayerRole>()
					where r != null
					orderby r.PrivilegeLevel descending
					select r).ThenBy((IPlayerRole r) => r.Code, StringComparer.OrdinalIgnoreCase)
				select new PermissionRoleSummary
				{
					Code = r.Code,
					Name = (r.Name ?? r.Code),
					Description = (r.Description ?? ""),
					PrivilegeLevel = r.PrivilegeLevel,
					Privileges = (r.Privileges ?? new List<string>()).Concat(r.RuntimePrivileges ?? new HashSet<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase)
						.ToList()
				}).ToList(),
			Commands = new List<PlayerCommandStatus>
			{
				BuildCommandStatus("spawn", "/spawn", IsCommandEnabled("spawn", config.SpawnEnabled), "/nse spawn", "Teleport to the authoritative global world spawn."),
				BuildCommandStatus("msg", "/msg <player> <message>", IsCommandEnabled("msg", config.PrivateMessagesEnabled), "/nse msg <player> <message>", "Send a private message to an online player."),
				BuildCommandStatus("reply", "/r <message>", IsCommandEnabled("reply", config.PrivateMessagesEnabled && config.ReplyEnabled), "/nse r <message>", "Reply to the last private-message correspondent."),
				BuildStarterKitCommandStatus(),
				BuildCommandStatus("warp", "/warp <name>", IsCommandEnabled("warp", config.WarpsEnabled && config.WarpCommandEnabled), "/nse warp <name>", "Teleport to a configured server warp. Optional per-warp item costs, cooldowns and warm-up rules are supported."),
				BuildCommandStatus("back", "/back", IsCommandEnabled("back", config.BackEnabled), "/nse back", "Return to the most recent recorded death or Suite teleport origin."),
				BuildCommandStatus("home", "/home [name]", IsCommandEnabled("home", config.HomesEnabled), "/nse home [name]", "Teleport to a saved home."),
				BuildCommandStatus("sethome", "/sethome [name]", IsCommandEnabled("sethome", config.HomesEnabled), "/nse sethome [name]", "Create or move a home within your configured allowance."),
				BuildCommandStatus("delhome", "/delhome [name]", IsCommandEnabled("delhome", config.HomesEnabled), "/nse delhome [name]", "Delete a saved home."),
				BuildCommandStatus("homes", "/homes", IsCommandEnabled("homes", config.HomesEnabled), "/nse homes", "List saved homes and the effective home limit."),
				BuildCommandStatus("t2p", "/t2p <player>", IsCommandEnabled("t2p", config.T2pEnabled), "/nse t2p <player>", "Teleport yourself to an online player. Access and cooldowns are role-configurable; roles without an explicit policy require controlserver/root by default."),
				BuildCommandStatus("t2m", "/t2m <player>", IsCommandEnabled("t2m", config.T2mEnabled), "/nse t2m <player>", "Send a teleport request to an online player. They must use /tpaccept, then wait 10 seconds before being moved to you. Access and cooldowns are role-configurable; roles without an explicit policy require controlserver/root by default."),
				BuildCommandStatus("rtp", "/rtp", IsCommandEnabled("rtp", config.RtpEnabled), "/nse rtp", "Teleport to a safe random overworld location within the configured radius.")
			}
		};
	}

	public PlayerCommandSaveResult SaveSettings(PlayerCommandSettingsRequest? request)
	{
		if (request == null)
		{
			return new PlayerCommandSaveResult
			{
				Success = false,
				Message = "Player command settings are required.",
				Snapshot = GetSnapshot()
			};
		}
		if (request.RolePolicyRevision != null && request.RolePolicyRevision != RoleManagementService.PolicyRevision(config))
		{
			return new PlayerCommandSaveResult
			{
				Success = false,
				Message = "Shared role policies changed elsewhere. Reload settings before saving; your draft has been kept.",
				Snapshot = GetSnapshot()
			};
		}
		config.SpawnEnabled = request.SpawnEnabled;
		config.SpawnCooldownSeconds = request.SpawnCooldownSeconds;
		config.SpawnWarmupSeconds = request.SpawnWarmupSeconds;
		config.SpawnCancelOnMove = request.SpawnCancelOnMove;
		config.PrivateMessagesEnabled = request.PrivateMessagesEnabled;
		config.ReplyEnabled = request.ReplyEnabled;
		config.RespectSuiteMutes = request.RespectSuiteMutes;
		config.MaxPrivateMessageLength = request.MaxPrivateMessageLength;
		config.StarterKitEnabled = request.StarterKitEnabled;
		config.StarterKitProvider = request.StarterKitProvider;
		config.WarpsEnabled = request.WarpsEnabled;
		config.WarpCommandEnabled = request.WarpCommandEnabled;
		config.WarpDefaultCooldownSeconds = request.WarpDefaultCooldownSeconds ?? config.WarpDefaultCooldownSeconds;
		config.WarpDefaultWarmupSeconds = request.WarpDefaultWarmupSeconds ?? config.WarpDefaultWarmupSeconds;
		config.WarpDefaultCancelOnMove = request.WarpDefaultCancelOnMove ?? config.WarpDefaultCancelOnMove;
		config.BackEnabled = request.BackEnabled;
		config.BackCooldownSeconds = request.BackCooldownSeconds;
		config.BackWarmupSeconds = request.BackWarmupSeconds;
		config.BackCancelOnMove = request.BackCancelOnMove;
		config.BackTrackDeaths = request.BackTrackDeaths;
		config.BackTrackTeleports = request.BackTrackTeleports;
		config.HomesEnabled = request.HomesEnabled;
		config.HomeGlobalLimit = request.HomeGlobalLimit;
		config.HomeRoleLimits = request.HomeRoleLimits ?? config.HomeRoleLimits;
		config.HomePlayerLimits = NormalizePlayerLimitKeys(request.HomePlayerLimits ?? new Dictionary<string, int>());
		config.HomeCooldownSeconds = request.HomeCooldownSeconds;
		config.HomeWarmupSeconds = request.HomeWarmupSeconds;
		config.HomeCancelOnMove = request.HomeCancelOnMove;
		config.T2pEnabled = request.T2pEnabled;
		config.T2pCooldownSeconds = request.T2pCooldownSeconds;
		config.T2mEnabled = request.T2mEnabled;
		config.T2mCooldownSeconds = request.T2mCooldownSeconds;
		config.RtpEnabled = request.RtpEnabled;
		config.RtpMinRadius = request.RtpMinRadius;
		config.RtpMaxRadius = request.RtpMaxRadius;
		config.RtpCooldownSeconds = request.RtpCooldownSeconds;
		config.RtpWarmupSeconds = request.RtpWarmupSeconds;
		config.RtpCancelOnMove = request.RtpCancelOnMove;
		config.RtpCostEnabled = request.RtpCostEnabled;
		config.RtpCostItemCode = request.RtpCostItemCode;
		config.RtpCostQuantity = request.RtpCostQuantity;
		config.RtpMaxSearchAttempts = request.RtpMaxSearchAttempts;
		config.RtpAvoidWaterAndLava = request.RtpAvoidWaterAndLava;
		config.RtpCenterOnPlayerPosition = request.RtpCenterOnPlayerPosition;
		config.TeleportRolePolicies = request.TeleportRolePolicies ?? config.TeleportRolePolicies;
		config.Clamp();
		SuiteConfigStorage.Store(api, config, "NewfiesPlayerCommands.json");
		api.Logger.Notification("[ServerEssentials/PlayerCommands] Runtime settings updated. spawn={0}, msg={1}, reply={2}, cooldown={3}s, respectMutes={4}, maxPmLength={5}, starterkit={6}, starterKitProvider={7}, warps={8}, warpCommand={9}.", config.SpawnEnabled, config.PrivateMessagesEnabled, config.ReplyEnabled, config.SpawnCooldownSeconds, config.RespectSuiteMutes, config.MaxPrivateMessageLength, config.StarterKitEnabled, StarterKitEffectiveProvider, config.WarpsEnabled, config.WarpCommandEnabled);
		return new PlayerCommandSaveResult
		{
			Success = true,
			Message = "Player command settings saved and applied live.",
			Snapshot = GetSnapshot()
		};
	}

	public PermissionsManagerSnapshot GetPermissionsSnapshot()
	{
		List<PermissionRoleSummary> list = (from r in (from r in api.Server.Config.Roles ?? new List<IPlayerRole>()
				where r != null
				orderby r.PrivilegeLevel descending
				select r).ThenBy((IPlayerRole r) => r.Code, StringComparer.OrdinalIgnoreCase)
			select new PermissionRoleSummary
			{
				Code = r.Code,
				Name = (r.Name ?? r.Code),
				Description = (r.Description ?? ""),
				PrivilegeLevel = r.PrivilegeLevel,
				Privileges = (r.Privileges ?? new List<string>()).Concat(r.RuntimePrivileges ?? new HashSet<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase)
					.ToList()
			}).ToList();
		List<string> knownPrivileges = Privilege.AllCodes().Concat(list.SelectMany((PermissionRoleSummary r) => r.Privileges)).Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase)
			.ToList();
		List<PermissionPlayerSummary> onlinePlayers = (from x in api.World.AllOnlinePlayers.OfType<IServerPlayer>().OrderBy((IServerPlayer x) => x.PlayerName, StringComparer.OrdinalIgnoreCase)
			select new PermissionPlayerSummary
			{
				PlayerUid = x.PlayerUID,
				PlayerName = x.PlayerName,
				RoleCode = (x.ServerData?.RoleCode ?? x.Role?.Code ?? ""),
				EffectivePrivileges = (x.Privileges ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string v) => v, StringComparer.OrdinalIgnoreCase).ToList(),
				PermanentPrivileges = (x.ServerData?.PermaPrivileges ?? new HashSet<string>()).OrderBy((string v) => v, StringComparer.OrdinalIgnoreCase).ToList(),
				DeniedPrivileges = (x.ServerData?.DeniedPrivileges ?? new HashSet<string>()).OrderBy((string v) => v, StringComparer.OrdinalIgnoreCase).ToList()
			}).ToList();
		return new PermissionsManagerSnapshot
		{
			Roles = list,
			OnlinePlayers = onlinePlayers,
			KnownPrivileges = knownPrivileges
		};
	}

	public PermissionChangeResult ChangePermission(PermissionChangeRequest? request)
	{
		if (request == null || string.IsNullOrWhiteSpace(request.PlayerUid))
		{
			return new PermissionChangeResult
			{
				Success = false,
				Message = "Player is required.",
				Snapshot = GetPermissionsSnapshot()
			};
		}
		IServerPlayer serverPlayer = OnlinePlayerByUid(request.PlayerUid.Trim());
		if (serverPlayer == null)
		{
			return new PermissionChangeResult
			{
				Success = false,
				Message = "Player must be online to change roles or direct privileges safely.",
				Snapshot = GetPermissionsSnapshot()
			};
		}
		string text = (request.Action ?? "").Trim().ToLowerInvariant();
		string text2 = (request.Value ?? "").Trim();
		bool flag = true;
		string message;
		switch (text)
		{
		case "set-role":
			if (api.Permissions.GetRole(text2) == null)
			{
				flag = false;
				message = "Unknown server role.";
				break;
			}
			api.Permissions.SetRole(serverPlayer, text2);
			message = $"Set {serverPlayer.PlayerName} role to {text2}.";
			break;
		case "grant":
			flag = api.Permissions.GrantPrivilege(serverPlayer.PlayerUID, text2, permanent: true);
			message = (flag ? $"Granted {text2} permanently to {serverPlayer.PlayerName}." : "Privilege grant failed.");
			break;
		case "revoke":
			flag = api.Permissions.RevokePrivilege(serverPlayer.PlayerUID, text2, permanent: true);
			message = (flag ? $"Revoked direct permanent privilege {text2} from {serverPlayer.PlayerName}." : "Privilege revoke failed.");
			break;
		case "deny":
			flag = api.Permissions.DenyPrivilege(serverPlayer.PlayerUID, text2);
			message = (flag ? $"Denied {text2} for {serverPlayer.PlayerName}." : "Privilege denial failed.");
			break;
		case "undeny":
			flag = api.Permissions.RemovePrivilegeDenial(serverPlayer.PlayerUID, text2);
			message = (flag ? $"Removed denial for {text2} from {serverPlayer.PlayerName}." : "Privilege denial removal failed.");
			break;
		default:
			flag = false;
			message = "Unknown permission action.";
			break;
		}
		return new PermissionChangeResult
		{
			Success = flag,
			Message = message,
			Snapshot = GetPermissionsSnapshot()
		};
	}

	public void HandleSpawn(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("spawn", config.SpawnEnabled))
		{
			Reply(player, groupId, "The Server Essentials /spawn command is disabled.", success: false);
			return;
		}
		AdminService adminService = getAdmin();
		if (adminService != null && adminService.IsJailed(player.PlayerUID))
		{
			Reply(player, groupId, "You cannot use /spawn while jailed.", success: false);
		}
		else
		{
			teleports.HandleSpawn(player, groupId, args);
		}
	}

	public void HandleStarterKit(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("starterkit", config.StarterKitEnabled))
		{
			Reply(player, groupId, "The Server Essentials starter kit is disabled.", success: false);
			return;
		}
		if (!UsesNseStarterKit)
		{
			Reply(player, groupId, "Starter kits are delegated to Th3Essentials. Use its /starterkit command instead.", success: false);
			return;
		}
		if (starterKit.Items.Count == 0)
		{
			Reply(player, groupId, "No NSE starter kit has been configured yet. An admin can fill their hotbar and run /setstarterkit.", success: false);
			return;
		}
		if (starterKitClaims.Contains(player.PlayerUID))
		{
			Reply(player, groupId, "You have already claimed the starter kit.", success: false);
			return;
		}
		IInventory hotbarInventory = player.InventoryManager.GetHotbarInventory();
		if (hotbarInventory == null)
		{
			Reply(player, groupId, "Your hotbar inventory is unavailable.", success: false);
			return;
		}
		List<(int, ItemStack)> list = new List<(int, ItemStack)>();
		foreach (StarterKitItemDefinition item in starterKit.Items.OrderBy((StarterKitItemDefinition x) => x.Slot))
		{
			if (item.Slot < 0 || item.Slot >= hotbarInventory.Count)
			{
				Reply(player, groupId, $"Starter kit slot {item.Slot + 1} is not available for your hotbar.", success: false);
				return;
			}
			ItemSlot itemSlot = hotbarInventory[item.Slot];
			if (itemSlot == null || itemSlot.Itemstack != null)
			{
				Reply(player, groupId, $"Clear hotbar slot {item.Slot + 1} before claiming the starter kit. The kit is placed atomically into its configured hotbar slots.", success: false);
				return;
			}
			ItemStack itemStack;
			try
			{
				itemStack = new ItemStack(Convert.FromBase64String(item.StackBase64));
				if (!itemStack.ResolveBlockOrItem(api.World))
				{
					Reply(player, groupId, "Starter kit item '" + item.Code + "' is no longer available on this server.", success: false);
					return;
				}
			}
			catch
			{
				Reply(player, groupId, $"Starter kit item in slot {item.Slot + 1} could not be loaded. Ask an admin to run /setstarterkit again.", success: false);
				return;
			}
			if (!itemSlot.CanHold(new DummySlot(itemStack)))
			{
				Reply(player, groupId, $"Hotbar slot {item.Slot + 1} cannot accept '{item.Code}'.", success: false);
				return;
			}
			list.Add((item.Slot, itemStack));
		}
		List<int> list2 = new List<int>();
		try
		{
			foreach (var item2 in list)
			{
				hotbarInventory[item2.Item1].Itemstack = item2.Item2;
				hotbarInventory[item2.Item1].MarkDirty();
				list2.Add(item2.Item1);
			}
			player.InventoryManager.BroadcastHotbarSlot();
			starterKitClaims.Add(player.PlayerUID);
			SaveStarterKitUsage();
			Reply(player, groupId, $"Starter kit claimed ({list.Count} occupied hotbar slot(s)).", success: true);
		}
		catch (Exception ex)
		{
			foreach (int item3 in list2)
			{
				try
				{
					hotbarInventory[item3].Itemstack = null;
					hotbarInventory[item3].MarkDirty();
				}
				catch
				{
				}
			}
			api.Logger.Error("[ServerEssentials/PlayerCommands] Starter kit claim failed for {0}: {1}", player.PlayerName, ex);
			Reply(player, groupId, "The starter kit could not be applied; no claim was recorded. Check the server log.", success: false);
		}
	}

	public void HandleSetStarterKit(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!UsesNseStarterKit)
		{
			Reply(player, groupId, "Starter kits are delegated to Th3Essentials. Use Th3Essentials /setstarterkit instead, or change the NSE Starter Kit Provider to NSE.", success: false);
			return;
		}
		IInventory hotbarInventory = player.InventoryManager.GetHotbarInventory();
		if (hotbarInventory == null)
		{
			Reply(player, groupId, "Your hotbar inventory is unavailable.", success: false);
			return;
		}
		List<StarterKitItemDefinition> list = new List<StarterKitItemDefinition>();
		for (int i = 0; i < hotbarInventory.Count; i++)
		{
			ItemStack itemStack = hotbarInventory[i]?.Itemstack;
			if (itemStack != null)
			{
				try
				{
					list.Add(new StarterKitItemDefinition
					{
						Slot = i,
						StackBase64 = Convert.ToBase64String(itemStack.ToBytes()),
						Code = (itemStack.Collectible?.Code?.ToString() ?? "unknown"),
						StackSize = itemStack.StackSize
					});
				}
				catch (Exception ex)
				{
					Reply(player, groupId, $"Could not serialize hotbar slot {i + 1}: {ex.Message}", success: false);
					return;
				}
			}
		}
		if (list.Count == 0)
		{
			Reply(player, groupId, "Your hotbar is empty. Put the desired starter-kit items in your hotbar, then run /setstarterkit.", success: false);
			return;
		}
		starterKit = new StarterKitDefinition
		{
			SchemaVersion = 1,
			UpdatedBy = player.PlayerName,
			UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Items = list
		};
		SuiteConfigStorage.Store(api, starterKit, "NewfiesStarterKit.json");
		Reply(player, groupId, $"NSE starter kit saved from {list.Count} occupied hotbar slot(s). Existing claim history was not reset.", success: true);
	}

	public void HandleResetStarterKitUsage(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!UsesNseStarterKit)
		{
			Reply(player, groupId, "Starter-kit usage is delegated to Th3Essentials.", success: false);
			return;
		}
		string text = (args.PopWord() ?? "").Trim();
		if (text.Length == 0)
		{
			Reply(player, groupId, "Usage: /resetstarterkitusage <online player>", success: false);
			return;
		}
		IServerPlayer serverPlayer = OnlinePlayerByName(text);
		if (serverPlayer == null)
		{
			Reply(player, groupId, "That player must be online so their permanent PlayerUID can be resolved safely.", success: false);
			return;
		}
		bool flag = starterKitClaims.Remove(serverPlayer.PlayerUID);
		if (flag)
		{
			SaveStarterKitUsage();
		}
		Reply(player, groupId, flag ? ("Starter-kit usage reset for " + serverPlayer.PlayerName + ".") : (serverPlayer.PlayerName + " had not claimed the NSE starter kit."), success: true);
	}

	public void HandleResetStarterKitUsageAll(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!UsesNseStarterKit)
		{
			Reply(player, groupId, "Starter-kit usage is delegated to Th3Essentials.", success: false);
			return;
		}
		int count = starterKitClaims.Count;
		starterKitClaims.Clear();
		SaveStarterKitUsage();
		Reply(player, groupId, $"Starter-kit usage reset for all players ({count} recorded claim(s) cleared).", success: true);
	}

	public void HandleMsg(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("msg", config.PrivateMessagesEnabled))
		{
			Reply(player, groupId, "Private messages are disabled by Server Essentials.", success: false);
			return;
		}
		string text = (args.PopWord() ?? "").Trim();
		string text2 = (args.PopAll() ?? "").Trim();
		if (text.Length == 0 || text2.Length == 0)
		{
			Reply(player, groupId, "Usage: /msg <player> <message>", success: false);
		}
		else
		{
			SendPrivateMessage(player, groupId, text, text2);
		}
	}

	public void HandleReply(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("reply", config.PrivateMessagesEnabled && config.ReplyEnabled))
		{
			Reply(player, groupId, "Private-message replies are disabled by Server Essentials.", success: false);
			return;
		}
		string text = (args.PopAll() ?? "").Trim();
		if (text.Length == 0)
		{
			Reply(player, groupId, "Usage: /r <message>", success: false);
			return;
		}
		if (!lastCorrespondentByUid.TryGetValue(player.PlayerUID, out string value) || string.IsNullOrWhiteSpace(value))
		{
			Reply(player, groupId, "You do not have anyone to reply to yet.", success: false);
			return;
		}
		IServerPlayer serverPlayer = OnlinePlayerByUid(value);
		if (serverPlayer == null)
		{
			Reply(player, groupId, "Your last private-message correspondent is no longer online.", success: false);
		}
		else
		{
			SendPrivateMessage(player, groupId, serverPlayer.PlayerName, text);
		}
	}

	public void HandleBack(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("back", config.BackEnabled))
		{
			Reply(player, groupId, "The Server Essentials /back command is disabled.", success: false);
		}
		else if (warps.HasPending(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a warp in progress.", success: false);
		}
		else
		{
			teleports.HandleBack(player, groupId, args);
		}
	}

	public void HandleHome(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("home", config.HomesEnabled))
		{
			Reply(player, groupId, "The Server Essentials /home command is disabled.", success: false);
		}
		else if (warps.HasPending(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a warp in progress.", success: false);
		}
		else
		{
			teleports.HandleHome(player, groupId, args);
		}
	}

	public void HandleSetHome(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("sethome", config.HomesEnabled))
		{
			Reply(player, groupId, "The Server Essentials /sethome command is disabled.", success: false);
		}
		else
		{
			teleports.HandleSetHome(player, groupId, args);
		}
	}

	public void HandleDeleteHome(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("delhome", config.HomesEnabled))
		{
			Reply(player, groupId, "The Server Essentials /delhome command is disabled.", success: false);
		}
		else
		{
			teleports.HandleDeleteHome(player, groupId, args);
		}
	}

	public void HandleHomes(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("homes", config.HomesEnabled))
		{
			Reply(player, groupId, "The Server Essentials /homes command is disabled.", success: false);
		}
		else
		{
			teleports.HandleHomes(player, groupId, args);
		}
	}

	public void HandleT2p(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("t2p", config.T2pEnabled))
		{
			Reply(player, groupId, "The Server Essentials /t2p command is disabled.", success: false);
		}
		else if (warps.HasPending(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a warp in progress.", success: false);
		}
		else
		{
			teleports.HandleT2p(player, groupId, args);
		}
	}

	public void HandleT2m(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("t2m", config.T2mEnabled))
		{
			Reply(player, groupId, "The Server Essentials /t2m command is disabled.", success: false);
		}
		else
		{
			teleports.HandleT2m(player, groupId, args);
		}
	}

	public void HandleTpAccept(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("t2m", config.T2mEnabled))
		{
			Reply(player, groupId, "The Server Essentials /t2m command is disabled.", success: false);
		}
		else
		{
			teleports.HandleTpAccept(player, groupId, args);
		}
	}

	public void HandleRtp(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("rtp", config.RtpEnabled))
		{
			Reply(player, groupId, "The Server Essentials /rtp command is disabled.", success: false);
		}
		else if (warps.HasPending(player.PlayerUID))
		{
			Reply(player, groupId, "You already have a warp in progress.", success: false);
		}
		else
		{
			teleports.HandleRtp(player, groupId, args);
		}
	}

	public void HandleWarp(IServerPlayer player, int groupId, CmdArgs args)
	{
		if (!IsCommandEnabled("warp", config.WarpsEnabled && config.WarpCommandEnabled))
		{
			Reply(player, groupId, "The Server Essentials /warp command is disabled.", success: false);
		}
		else if (teleports.HasPending(player.PlayerUID))
		{
			Reply(player, groupId, "You already have another teleport in progress.", success: false);
		}
		else
		{
			warps.HandleWarp(player, groupId, args);
		}
	}

	public void HandleWarps(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleWarps(player, groupId, args);
	}

	public void HandleWarpInfo(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleWarpInfo(player, groupId, args);
	}

	public void HandleSetWarp(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleSetWarp(player, groupId, args);
	}

	public void HandleDeleteWarp(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleDeleteWarp(player, groupId, args);
	}

	public void HandleSetWarpCost(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleSetWarpCost(player, groupId, args);
	}

	public void HandleSetWarpCooldown(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleSetWarpCooldown(player, groupId, args);
	}

	public void HandleSetWarpWarmup(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleSetWarpWarmup(player, groupId, args);
	}

	public void HandleSetWarpMovement(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleSetWarpMovement(player, groupId, args);
	}

	public void HandleReloadWarps(IServerPlayer player, int groupId, CmdArgs args)
	{
		warps.HandleReloadWarps(player, groupId, args);
	}

	private Dictionary<string, int> NormalizePlayerLimitKeys(Dictionary<string, int> input)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, int> item in input)
		{
			string text = (item.Key ?? "").Trim();
			if (text.Length != 0)
			{
				dictionary[(getAdmin()?.GetPlayer(text))?.PlayerUid ?? text] = item.Value;
			}
		}
		return dictionary;
	}

	private void SendPrivateMessage(IServerPlayer sender, int groupId, string targetName, string message)
	{
		if (config.RespectSuiteMutes)
		{
			AdminService adminService = getAdmin();
			if (adminService != null && adminService.IsMuted(sender.PlayerUID, out PlayerAdminRecord record))
			{
				string text = (string.IsNullOrWhiteSpace(record?.MuteReason) ? "Muted by staff" : record.MuteReason);
				Reply(sender, groupId, "You are muted and cannot send private messages. Reason: " + text, success: false);
				return;
			}
		}
		if (message.Length > config.MaxPrivateMessageLength)
		{
			Reply(sender, groupId, $"Private messages are limited to {config.MaxPrivateMessageLength} characters.", success: false);
			return;
		}
		IServerPlayer serverPlayer = OnlinePlayerByName(targetName);
		if (serverPlayer == null)
		{
			Reply(sender, groupId, "Player '" + targetName + "' is not online.", success: false);
			return;
		}
		if (string.Equals(sender.PlayerUID, serverPlayer.PlayerUID, StringComparison.OrdinalIgnoreCase))
		{
			Reply(sender, groupId, "You cannot private-message yourself.", success: false);
			return;
		}
		lastCorrespondentByUid[sender.PlayerUID] = serverPlayer.PlayerUID;
		lastCorrespondentByUid[serverPlayer.PlayerUID] = sender.PlayerUID;
		serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup, "[PM from " + sender.PlayerName + "] " + message, EnumChatType.Notification);
		sender.SendMessage(groupId, "[PM to " + serverPlayer.PlayerName + "] " + message, EnumChatType.CommandSuccess);
	}

	private PlayerCommandStatus BuildStarterKitCommandStatus()
	{
		bool hasTh3Essentials = HasTh3Essentials;
		bool flag = suiteOwnedRootCommands.Contains("starterkit");
		string rootProvider;
		if (!hasTh3Essentials)
		{
			if (flag)
			{
				rootProvider = "Newfie's Server Essentials";
			}
			else
			{
				rootProvider = ((api.ChatCommands.Get("starterkit") == null) ? "Unregistered" : "Another command provider");
			}
		}
		else
		{
			rootProvider = ((StarterKitEffectiveProvider == "Th3Essentials") ? "Th3Essentials (selected provider)" : "Th3Essentials owns root; NSE available as /nse starterkit");
			flag = false;
		}
		return new PlayerCommandStatus
		{
			Key = "starterkit",
			Command = "/starterkit",
			Enabled = IsCommandEnabled("starterkit", config.StarterKitEnabled && UsesNseStarterKit),
			IndividuallyEnabled = IsIndividualCommandEnabled("starterkit"),
			RootProvider = rootProvider,
			RootOwnedBySuite = flag,
			FallbackCommand = "/nse starterkit",
			Description = "Claim the configured starter kit once. Effective provider: " + StarterKitEffectiveProvider + ".",
			RequiredPrivilege = Privilege.chat
		};
	}

	private void LoadStarterKitUsage()
	{
		starterKitClaims.Clear();
		try
		{
			byte[] data = api.WorldManager.SaveGame.GetData("newfiesserveressentials:starterkit-usage-v1");
			if (data == null || data.Length == 0)
			{
				return;
			}
			foreach (string item in JsonSerializer.Deserialize<StarterKitUsageDatabase>(Encoding.UTF8.GetString(data))?.ClaimedPlayerUids ?? new List<string>())
			{
				if (!string.IsNullOrWhiteSpace(item))
				{
					starterKitClaims.Add(item);
				}
			}
		}
		catch (Exception ex)
		{
			api.Logger.Error("[ServerEssentials/PlayerCommands] Failed to load starter-kit usage database. Existing save data was left untouched. {0}", ex);
		}
	}

	private void SaveStarterKitUsage()
	{
		StarterKitUsageDatabase value = new StarterKitUsageDatabase
		{
			ClaimedPlayerUids = starterKitClaims.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).ToList()
		};
		api.WorldManager.SaveGame.StoreData("newfiesserveressentials:starterkit-usage-v1", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
	}

	private PlayerCommandStatus BuildCommandStatus(string key, string command, bool enabled, string fallback, string description)
	{
		string text = ((key == "reply") ? "r" : key);
		bool hasTh3Essentials = HasTh3Essentials;
		bool flag = suiteOwnedRootCommands.Contains(text);
		string rootProvider;
		if (!hasTh3Essentials || (!text.Equals("spawn", StringComparison.OrdinalIgnoreCase) && !text.Equals("msg", StringComparison.OrdinalIgnoreCase) && !text.Equals("r", StringComparison.OrdinalIgnoreCase)))
		{
			if (flag)
			{
				rootProvider = "Newfie's Server Essentials";
			}
			else
			{
				rootProvider = ((api.ChatCommands.Get(text) == null) ? "Unregistered" : "Another command provider");
			}
		}
		else
		{
			rootProvider = ((api.ChatCommands.Get(text) != null) ? "External provider (Th3Essentials compatibility mode)" : "Reserved for Th3Essentials compatibility");
			flag = false;
		}
		string requiredPrivilege = ((key.Equals("t2p", StringComparison.OrdinalIgnoreCase) || key.Equals("t2m", StringComparison.OrdinalIgnoreCase)) ? "role policy; default controlserver/root" : Privilege.chat);
		return new PlayerCommandStatus
		{
			Key = key,
			Command = command,
			Enabled = enabled,
			IndividuallyEnabled = IsIndividualCommandEnabled(key),
			RootProvider = rootProvider,
			RootOwnedBySuite = flag,
			FallbackCommand = fallback,
			Description = description,
			RequiredPrivilege = requiredPrivilege
		};
	}

	private IServerPlayer? OnlinePlayerByName(string name)
	{
		return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));
	}

	private IServerPlayer? OnlinePlayerByUid(string uid)
	{
		return api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault((IServerPlayer p) => string.Equals(p.PlayerUID, uid, StringComparison.OrdinalIgnoreCase));
	}

	private void SafeRegisterRoot(string command, string description, string syntax, ServerChatCommandDelegate handler, string requiredPrivilege)
	{
		try
		{
			if (api.ChatCommands.Get(command) != null)
			{
				api.Logger.Notification("[ServerEssentials/PlayerCommands] Root /{0} is already owned by another provider. NSE left it untouched.", command);
			}
			else
			{
				api.RegisterCommand(command, description, syntax, handler, requiredPrivilege);
				suiteOwnedRootCommands.Add(command);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/PlayerCommands] Could not register root /{0}; leaving any existing provider untouched. {1}: {2}", command, ex.GetType().Name, ex.Message);
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		lastCorrespondentByUid.Remove(player.PlayerUID);
		string[] array = (from x in lastCorrespondentByUid
			where string.Equals(x.Value, player.PlayerUID, StringComparison.OrdinalIgnoreCase)
			select x.Key).ToArray();
		foreach (string key in array)
		{
			lastCorrespondentByUid.Remove(key);
		}
	}

	private static void Reply(IServerPlayer player, int groupId, string message, bool success)
	{
		player.SendMessage(groupId, message, (!success) ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}

	public void Dispose()
	{
		if (!disposed)
		{
			disposed = true;
			api.Event.PlayerDisconnect -= OnPlayerDisconnect;
			lastCorrespondentByUid.Clear();
			suiteOwnedRootCommands.Clear();
			warps.Dispose();
			teleports.Dispose();
		}
	}
}
