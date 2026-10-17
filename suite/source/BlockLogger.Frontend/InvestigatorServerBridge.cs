using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlockLogger.Admin;
using BlockLogger.AnimalLimiter;
using BlockLogger.AntiCheat;
using BlockLogger.Config;
using BlockLogger.Integrations;
using BlockLogger.LoginProtection;
using BlockLogger.Models;
using BlockLogger.Protocol;
using BlockLogger.Reports;
using BlockLogger.Restarts;
using BlockLogger.ServerHealth;
using BlockLogger.Services;
using BlockLogger.WebDashboard;
using BlockLogger.WebMap;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Frontend;

public sealed class InvestigatorServerBridge
{
	public const string ChannelName = "blocklogger-investigator-v2";

	private const string InvestigatorPrivilege = "blocklogger";

	private readonly ICoreServerAPI api;

	private readonly BlockLoggerModule module;

	private readonly ConcurrentDictionary<string, byte> activeQueries = new ConcurrentDictionary<string, byte>();

	private readonly ConcurrentDictionary<string, long> mapTeleportLastRequestMs = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly SemaphoreSlim fingerprintResolutionGate = new SemaphoreSlim(1, 1);

	private IServerNetworkChannel? channel;

	public InvestigatorServerBridge(ICoreServerAPI api, BlockLoggerModule module)
	{
		this.api = api;
		this.module = module;
	}

	public void Register()
	{
		channel = api.Network.RegisterChannel("blocklogger-investigator-v2").RegisterMessageType<InvestigatorQueryRequest>().RegisterMessageType<InvestigatorQueryResponse>()
			.RegisterMessageType<InvestigatorStatusRequest>()
			.RegisterMessageType<InvestigatorStatusResponse>()
			.RegisterMessageType<BlockLoggerSettingsRequest>()
			.RegisterMessageType<BlockLoggerSettingsResponse>()
			.RegisterMessageType<AnimalLimiterRequest>()
			.RegisterMessageType<AnimalLimiterResponse>()
			.RegisterMessageType<ProfilerRequest>()
			.RegisterMessageType<ProfilerResponse>()
			.RegisterMessageType<LoginProtectionRequest>()
			.RegisterMessageType<LoginProtectionResponse>()
			.RegisterMessageType<WebDashboardRequest>()
			.RegisterMessageType<WebDashboardResponse>()
			.RegisterMessageType<AntiCheatGuiRequest>()
			.RegisterMessageType<AntiCheatGuiResponse>()
			.RegisterMessageType<AdminCommandGuiRequest>()
			.RegisterMessageType<AdminCommandGuiResponse>()
			.RegisterMessageType<DiscordIntegrationGuiRequest>()
			.RegisterMessageType<DiscordIntegrationGuiResponse>()
			.SetMessageHandler<InvestigatorQueryRequest>(HandleQuery)
			.SetMessageHandler<InvestigatorStatusRequest>(HandleStatus)
			.SetMessageHandler<BlockLoggerSettingsRequest>(HandleBlockLoggerSettings)
			.SetMessageHandler<AnimalLimiterRequest>(HandleAnimalLimiter)
			.SetMessageHandler<ProfilerRequest>(HandleProfiler)
			.SetMessageHandler<LoginProtectionRequest>(HandleLoginProtection)
			.SetMessageHandler<WebDashboardRequest>(HandleWebDashboard)
			.SetMessageHandler<AntiCheatGuiRequest>(HandleAntiCheatGui)
			.SetMessageHandler<AdminCommandGuiRequest>(HandleAdminCommandGui)
			.SetMessageHandler<DiscordIntegrationGuiRequest>(HandleDiscordIntegration);
	}

	private void HandleDiscordIntegration(IServerPlayer player, DiscordIntegrationGuiRequest request)
	{
		if (channel == null)
		{
			return;
		}
		if (!player.HasPrivilege(Privilege.controlserver) && !player.HasPrivilege(Privilege.root))
		{
			channel.SendPacket(new DiscordIntegrationGuiResponse
			{
				RequestId = request.RequestId,
				Success = false,
				Message = "You need the controlserver privilege to view or change Discord Integration settings."
			}, player);
			return;
		}
		string text = (request.Operation ?? "get").Trim();
		bool flag = text.StartsWith("motd-", StringComparison.OrdinalIgnoreCase) || text.Equals("motd", StringComparison.OrdinalIgnoreCase);
		CommunityMotdControlSnapshot communityMotdControlSnapshot;
		DiscordControlSnapshot discordControlSnapshot;
		if (flag)
		{
			string text2 = text.ToLowerInvariant();
			if (1 == 0)
			{
			}
			string text3 = text2 switch
			{
				"motd-save" => "save", 
				"motd-apply" => "apply", 
				"motd-reload" => "reload", 
				"motd-resetseen" => "resetseen", 
				_ => "get", 
			};
			if (1 == 0)
			{
			}
			string operation = text3;
			communityMotdControlSnapshot = CommunityMotdControlBridge.Request(api, operation, request.StringValue ?? "");
			discordControlSnapshot = DiscordControlBridge.Request(api);
		}
		else
		{
			discordControlSnapshot = DiscordControlBridge.Request(api, text, request.SettingKey, request.BoolValue, request.IntValue, request.StringValue, request.Target);
			communityMotdControlSnapshot = CommunityMotdControlBridge.Request(api);
		}
		channel.SendPacket(new DiscordIntegrationGuiResponse
		{
			RequestId = request.RequestId,
			Success = (flag ? communityMotdControlSnapshot.Success : discordControlSnapshot.Success),
			Message = (flag ? communityMotdControlSnapshot.Message : discordControlSnapshot.Message),
			Settings = ToDiscordGuiSettings(discordControlSnapshot, communityMotdControlSnapshot)
		}, player);
	}

	private static DiscordIntegrationGuiSettingsPacket ToDiscordGuiSettings(DiscordControlSnapshot s, CommunityMotdControlSnapshot motd)
	{
		return new DiscordIntegrationGuiSettingsPacket
		{
			TransportAvailable = s.TransportAvailable,
			TransportEnabled = s.TransportEnabled,
			TransportConnected = s.TransportConnected,
			BotTokenConfigured = s.BotTokenConfigured,
			BotName = s.BotName,
			GuildId = s.GuildId,
			InviteUrl = s.InviteUrl,
			ChatEnabled = s.ChatEnabled,
			ChatChannelId = s.ChatChannelId,
			GameToDiscord = s.GameToDiscord,
			DiscordToGame = s.DiscordToGame,
			GameBoldSenderLabel = s.GameBoldSenderLabel,
			GameUseRoleColor = s.GameUseRoleColor,
			AdminAuditEnabled = s.AdminAuditEnabled,
			AdminAuditChannelId = s.AdminAuditChannelId,
			AntiCheatDiscordEnabled = s.AntiCheatDiscordEnabled,
			AntiCheatChannelId = s.AntiCheatChannelId,
			CommandsEnabled = s.CommandsEnabled,
			PlayerInfoCommandsEnabled = s.PlayerInfoCommandsEnabled,
			StaffCommandsEnabled = s.StaffCommandsEnabled,
			RestartCommandsEnabled = s.RestartCommandsEnabled,
			LinkCommandsEnabled = s.LinkCommandsEnabled,
			Channels = s.Channels,
			PlayerJoin = ToDiscordRoute(s.PlayerJoin),
			PlayerLeave = ToDiscordRoute(s.PlayerLeave),
			PlayerDeath = ToDiscordRoute(s.PlayerDeath),
			ServerStarted = ToDiscordRoute(s.ServerStarted),
			ServerStopping = ToDiscordRoute(s.ServerStopping),
			DiscordLinked = ToDiscordRoute(s.DiscordLinked),
			DiscordUnlinked = ToDiscordRoute(s.DiscordUnlinked),
			LinkAvailable = s.LinkAvailable,
			LinkEnabled = s.LinkEnabled,
			LinkCodeLength = s.LinkCodeLength,
			LinkCodeExpirySeconds = s.LinkCodeExpirySeconds,
			LinkCodeCooldownSeconds = s.LinkCodeCooldownSeconds,
			LinkAllowMultipleVs = s.LinkAllowMultipleVs,
			LinkLogChanges = s.LinkLogChanges,
			LinkedAccountCount = s.LinkedAccountCount,
			PendingLinkCodeCount = s.PendingLinkCodeCount,
			MotdAvailable = motd.Available,
			MotdVersion = (motd.Version ?? ""),
			MotdConfigJson = (motd.ConfigJson ?? "{}"),
			MotdMessage = (motd.Message ?? "")
		};
	}

	private static DiscordAnnouncementGuiPacket ToDiscordRoute(DiscordAnnouncementSnapshot route)
	{
		return new DiscordAnnouncementGuiPacket
		{
			Enabled = route.Enabled,
			ChannelId = route.ChannelId,
			Format = route.Format
		};
	}

	private void HandleAdminCommandGui(IServerPlayer player, AdminCommandGuiRequest request)
	{
		if (channel == null)
		{
			return;
		}
		AdminService admin = module.Admin;
		if (admin == null)
		{
			SendAdminCommandGui(player, request.RequestId, success: false, request.Operation, "Administration is disabled.", new List<string>());
			return;
		}
		string text = (request.Operation ?? "").Trim().ToLowerInvariant();
		string text2 = AdminPrivilegeForOperation(text);
		if (string.IsNullOrWhiteSpace(text2))
		{
			SendAdminCommandGui(player, request.RequestId, success: false, text, "Unknown admin action.", new List<string>());
			return;
		}
		bool flag = text == "map-teleport" && (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root));
		if (!player.HasPrivilege(text2) && !flag)
		{
			SendAdminCommandGui(player, request.RequestId, success: false, text, "You need the " + text2 + " privilege for this action.", new List<string>());
			return;
		}
		if (text == "map-teleport")
		{
			QueueAdminMapTeleport(player, request, admin);
			return;
		}
		string text3 = (request.Target ?? "").Trim();
		string text4 = (request.Value ?? "").Trim();
		string text5 = (request.Reason ?? "").Trim();
		string playerName = player.PlayerName;
		List<string> list = new List<string>();
		bool flag2 = true;
		string message;
		try
		{
			switch (text)
			{
			case "player-info":
			{
				AdminPlayerSummary player2 = admin.GetPlayer(string.IsNullOrWhiteSpace(text3) ? player.PlayerName : text3);
				if (player2 == null)
				{
					flag2 = false;
					message = "Player not found.";
					break;
				}
				message = $"{player2.Name} • {(player2.Online ? "Online" : "Offline")} • {player2.GameMode}";
				list.Add("UID: " + player2.PlayerUid);
				list.Add($"Role: {player2.Role} • Ping: {player2.PingMilliseconds} ms • Position: {player2.Position}");
				list.Add($"Health: {player2.Health:0.#}/{player2.MaxHealth:0.#} • Warnings: {player2.ActiveWarnings} • Muted: {player2.Muted} • Banned: {player2.Banned} • Frozen: {player2.Frozen} • Jailed: {player2.Jailed}");
				list.Add("Playtime: " + FormatAdminDuration(player2.TotalPlaySeconds) + " • Session: " + FormatAdminDuration(player2.SessionSeconds));
				break;
			}
			case "online":
			{
				IReadOnlyList<AdminPlayerSummary> onlinePlayerSummaries = admin.GetOnlinePlayerSummaries();
				message = $"{onlinePlayerSummaries.Count} player{((onlinePlayerSummaries.Count == 1) ? "" : "s")} online.";
				foreach (AdminPlayerSummary item in onlinePlayerSummaries.Take(100))
				{
					list.Add($"ADMINPLAYER\t{GuiField(item.PlayerUid)}\t{GuiField(item.Name)}\t{item.PingMilliseconds}\t{GuiField(item.GameMode.ToString())}\t{GuiField(item.Position)}\t{item.Health:0.#}/{item.MaxHealth:0.#}");
				}
				break;
			}
			case "seen":
			{
				AdminPlayerSummary player4 = admin.GetPlayer(text3);
				if (player4 == null)
				{
					flag2 = false;
					message = "Player not found.";
				}
				else
				{
					message = (player4.Online ? (player4.Name + " is online now.") : (player4.Name + " was last seen " + FormatAdminTime(player4.LastSeenUnixMilliseconds) + "."));
				}
				break;
			}
			case "playtime":
			{
				AdminPlayerSummary player6 = admin.GetPlayer(text3);
				if (player6 == null)
				{
					flag2 = false;
					message = "Player not found.";
					break;
				}
				message = $"{player6.Name}: {FormatAdminDuration(player6.TotalPlaySeconds)} total • {FormatAdminDuration(player6.SessionSeconds)} this session.";
				break;
			}
			case "afk":
			{
				AdminPlayerSummary player7 = admin.GetPlayer(text3);
				if (player7 == null)
				{
					flag2 = false;
					message = "Player not found.";
					break;
				}
				message = $"{player7.Name}: {(player7.Afk ? "AFK" : "Active")} • idle {FormatAdminDuration(player7.IdleSeconds)}.";
				break;
			}
			case "history":
			{
				List<AdminAuditRecord> history = admin.GetHistory(string.IsNullOrWhiteSpace(text3) ? player.PlayerName : text3, 80);
				message = ((history.Count == 0) ? "No admin history found." : $"Showing {history.Count} recent admin history entries.");
				foreach (AdminAuditRecord item2 in history)
				{
					list.Add($"{FormatAdminTime(item2.UnixMilliseconds)} • {item2.Action} • {item2.Actor} • {item2.Detail}");
				}
				break;
			}
			case "warnings":
			{
				List<AdminWarningRecord> warnings = admin.GetWarnings(text3);
				message = ((warnings.Count == 0) ? "No warnings found." : $"Showing {Math.Min(warnings.Count, 50)} warning records.");
				foreach (AdminWarningRecord item3 in warnings.Take(50))
				{
					list.Add($"#{item3.Id} • {(item3.Removed ? "REMOVED" : "ACTIVE")} • {FormatAdminTime(item3.UnixMilliseconds)} • {item3.IssuedBy}: {item3.Reason}");
				}
				break;
			}
			case "notes":
			{
				List<AdminNoteRecord> notes = admin.GetNotes(text3);
				message = ((notes.Count == 0) ? "No staff notes found." : $"Showing {Math.Min(notes.Count, 50)} staff notes.");
				foreach (AdminNoteRecord item4 in notes.Take(50))
				{
					list.Add($"#{item4.Id} • {(item4.Removed ? "REMOVED" : "ACTIVE")} • {FormatAdminTime(item4.UnixMilliseconds)} • {item4.Author}: {item4.Text}");
				}
				break;
			}
			case "heal":
				message = admin.Heal(playerName, text3, "shiftl");
				break;
			case "feed":
				message = admin.Feed(playerName, text3, "shiftl");
				break;
			case "revive":
				message = admin.Revive(playerName, text3, "shiftl");
				break;
			case "smite":
				message = admin.Smite(playerName, text3, text5, "shiftl");
				break;
			case "setjail":
				message = admin.SetJailPosition(player, "shiftl");
				break;
			case "jail":
				message = admin.Jail(playerName, text3, string.IsNullOrWhiteSpace(text4) ? "30m" : text4, text5, "shiftl");
				break;
			case "unjail":
				message = admin.Unjail(playerName, text3, text5, "shiftl");
				break;
			case "jailinfo":
				message = admin.JailInfo(text3);
				break;
			case "ghost":
				message = admin.ToggleGhost(player, "shiftl");
				break;
			case "warn":
				message = admin.Warn(playerName, text3, text5, "shiftl");
				break;
			case "unwarn":
				message = admin.RemoveWarning(playerName, text3, request.LongValue, "shiftl");
				break;
			case "mute":
				message = admin.Mute(playerName, text3, string.IsNullOrWhiteSpace(text4) ? "30m" : text4, text5, "shiftl");
				break;
			case "unmute":
				message = admin.Unmute(playerName, text3, "shiftl");
				break;
			case "muteinfo":
			{
				AdminPlayerSummary player5 = admin.GetPlayer(text3);
				if (player5 == null)
				{
					flag2 = false;
					message = "Player not found.";
				}
				else
				{
					message = (player5.Muted ? (player5.Name + " is muted" + ((player5.MuteUntilUnixMilliseconds > 0) ? (" until " + FormatAdminTime(player5.MuteUntilUnixMilliseconds)) : " permanently") + ".") : (player5.Name + " is not muted."));
				}
				break;
			}
			case "tempban":
				message = admin.TempBan(playerName, text3, string.IsNullOrWhiteSpace(text4) ? "1d" : text4, text5, "shiftl");
				break;
			case "unban":
				message = admin.Unban(playerName, text3, "shiftl");
				break;
			case "baninfo":
			{
				AdminPlayerSummary player3 = admin.GetPlayer(text3);
				if (player3 == null)
				{
					flag2 = false;
					message = "Player not found.";
				}
				else
				{
					message = (player3.Banned ? (player3.Name + " is temporarily banned until " + FormatAdminTime(player3.BanUntilUnixMilliseconds) + ".") : (player3.Name + " has no active suite temporary ban."));
				}
				break;
			}
			case "freeze":
				message = admin.Freeze(playerName, text3, string.IsNullOrWhiteSpace(text4) ? "permanent" : text4, text5, "shiftl");
				break;
			case "unfreeze":
				message = admin.Unfreeze(playerName, text3, "shiftl");
				break;
			case "kick":
				message = admin.Kick(playerName, text3, text5, "shiftl");
				break;
			case "note-add":
				message = admin.AddNote(playerName, text3, text5, "shiftl");
				break;
			case "note-remove":
				message = admin.RemoveNote(playerName, text3, request.LongValue, "shiftl");
				break;
			case "goto":
				message = admin.Goto(player, text3);
				break;
			case "bring":
				message = admin.Bring(player, text3);
				break;
			case "back":
				message = admin.Back(player);
				break;
			case "sendback":
				message = admin.SendBack(playerName, text3, "shiftl");
				break;
			case "invsee":
			{
				List<AdminInventorySlotView> inventory = admin.GetInventory(text3);
				List<AdminInventorySlotView> list2 = inventory.Where((AdminInventorySlotView x) => !x.Empty).ToList();
				if (inventory.Count == 0)
				{
					flag2 = false;
					message = "Player must be online or inventory is unavailable.";
					break;
				}
				message = $"Inventory for {text3}: {list2.Count} occupied / {inventory.Count} slots. Click a slot in the visual inventory to edit it.";
				AppendAdminInventoryGuiLines(admin, text3, list);
				break;
			}
			case "itemsearch":
			{
				List<AdminItemSearchResult> list4 = admin.SearchItems(text4, 80);
				message = ((list4.Count == 0) ? ("No items matched '" + text4 + "'.") : $"Item search for '{text4}' • {list4.Count} match{((list4.Count == 1) ? "" : "es")}. Select an item to copy its code into the editor.");
				foreach (AdminItemSearchResult item5 in list4)
				{
					list.Add($"ITEM\t{GuiField(item5.Code)}\t{GuiField(item5.Name)}\t{GuiField(item5.ItemClass)}\t{item5.MaxStackSize}");
				}
				break;
			}
			case "invsnapshot":
				message = admin.CreateInventorySnapshot(playerName, text3, string.IsNullOrWhiteSpace(text5) ? "Shift+L snapshot" : text5, "shiftl");
				break;
			case "invgive":
				message = admin.GiveInventoryItem(playerName, text3, text4, Math.Max(1, request.IntValue), "", -1, "", "shiftl");
				break;
			case "invgive-selected":
				message = admin.GiveInventoryItem(playerName, text3, text4, Math.Max(1, request.IntValue), request.Secondary, request.IntValue2, request.ExpectedFingerprint, "shiftl");
				break;
			case "invqty":
				message = admin.SetInventoryQuantity(playerName, text3, text4, request.IntValue, request.IntValue2, request.ExpectedFingerprint, "shiftl");
				break;
			case "invreplace":
				message = admin.ReplaceInventorySlot(playerName, text3, request.Secondary, request.IntValue, text4, request.IntValue2, request.ExpectedFingerprint, "shiftl");
				break;
			case "invremove":
				if (!request.Confirmed)
				{
					flag2 = false;
					message = "Confirmation required before removing an inventory slot.";
				}
				else
				{
					message = admin.RemoveInventorySlot(playerName, text3, text4, request.IntValue, "shiftl", request.ExpectedFingerprint);
				}
				break;
			case "invrestore":
				if (!request.Confirmed)
				{
					flag2 = false;
					message = "Confirmation required before restoring a snapshot.";
				}
				else
				{
					message = admin.RestoreInventorySnapshot(playerName, text3, request.LongValue, "shiftl");
				}
				break;
			case "laghere":
			{
				LagHereReport lagHereReport = admin.ScanLagHere(player, Math.Clamp((request.IntValue <= 0) ? 3 : request.IntValue, 1, 16));
				message = $"Lag scan: {lagHereReport.Entities} entities • {lagHereReport.DroppedItems} drops • {lagHereReport.LoadedChunks} chunks.";
				list.Add($"Radius: {lagHereReport.RadiusChunks} chunks • Main-thread delay: {lagHereReport.MainThreadDelayMs:0.#} ms • CPU: {lagHereReport.CpuPercent:0.#}% • BlockLogger queue: {lagHereReport.BlockLoggerQueue}");
				foreach (KeyValuePair<string, int> item6 in lagHereReport.TopEntityTypes.OrderByDescending((KeyValuePair<string, int> x) => x.Value).Take(20))
				{
					list.Add($"{item6.Key} • {item6.Value}");
				}
				break;
			}
			case "cleanup-scan":
			{
				CleanupReport cleanupReport2 = admin.ScanCleanup();
				message = $"Cleanup scan: {cleanupReport2.LoadedEntities} loaded entities • {cleanupReport2.DroppedItems} dropped items.";
				list.Add($"Creatures {cleanupReport2.Creatures} • Inanimate {cleanupReport2.InanimateEntities} • Dead/despawning {cleanupReport2.DeadOrDespawning}");
				break;
			}
			case "cleanup-drops":
			{
				if (!request.Confirmed)
				{
					flag2 = false;
					message = "Confirmation required before removing dropped items.";
					break;
				}
				CleanupReport cleanupReport = admin.CleanupDrops(dryRun: false);
				message = $"Removed {cleanupReport.Removed} dropped item entities.";
				break;
			}
			case "maintenance-on":
			{
				long milliseconds = 0L;
				if (!string.IsNullOrWhiteSpace(text4) && !AdminService.TryParseDuration(text4, out milliseconds, out var _))
				{
					flag2 = false;
					message = "Invalid duration. Examples: 30m, 6h, 2d.";
				}
				else
				{
					message = admin.SetMaintenance(playerName, enabled: true, string.IsNullOrWhiteSpace(text5) ? "Maintenance" : text5, milliseconds, "shiftl");
				}
				break;
			}
			case "maintenance-off":
				message = admin.SetMaintenance(playerName, enabled: false, text5, 0L, "shiftl");
				break;
			case "maintenance-status":
			{
				(bool, string, long) maintenance = admin.GetMaintenance();
				message = (maintenance.Item1 ? ("Maintenance ON • " + maintenance.Item2 + ((maintenance.Item3 > 0) ? (" • until " + FormatAdminTime(maintenance.Item3)) : "")) : "Maintenance is off.");
				break;
			}
			case "shutdown":
			case "restart":
			{
				long milliseconds2;
				bool permanent2;
				if (!request.Confirmed)
				{
					flag2 = false;
					message = "Confirmation required before scheduling a shutdown/restart.";
				}
				else if (!AdminService.TryParseDuration(string.IsNullOrWhiteSpace(text4) ? "5m" : text4, out milliseconds2, out permanent2) | permanent2)
				{
					flag2 = false;
					message = "Invalid delay. Examples: 1m, 5m, 30m.";
				}
				else
				{
					message = ((text == "restart") ? admin.ScheduleRestart(playerName, milliseconds2, text5, "shiftl") : admin.ScheduleShutdown(playerName, milliseconds2, text5, "shiftl"));
				}
				break;
			}
			case "shutdown-cancel":
				message = admin.CancelShutdown(playerName, "shiftl");
				break;
			case "autorestart-status":
			{
				AutomatedRestartService automatedRestarts5 = module.AutomatedRestarts;
				if (automatedRestarts5 == null)
				{
					flag2 = false;
					message = "Automated restarts are unavailable.";
				}
				else
				{
					message = automatedRestarts5.StatusText();
					AppendAutoRestartGuiLines(automatedRestarts5, list);
				}
				break;
			}
			case "autorestart-enable":
			case "autorestart-disable":
			{
				AutomatedRestartService automatedRestarts4 = module.AutomatedRestarts;
				if (automatedRestarts4 == null)
				{
					flag2 = false;
					message = "Automated restarts are unavailable.";
				}
				else
				{
					message = automatedRestarts4.SetEnabled(text == "autorestart-enable");
					AppendAutoRestartGuiLines(automatedRestarts4, list);
				}
				break;
			}
			case "autorestart-reload":
			{
				AutomatedRestartService automatedRestarts3 = module.AutomatedRestarts;
				if (automatedRestarts3 == null)
				{
					flag2 = false;
					message = "Automated restarts are unavailable.";
				}
				else
				{
					message = automatedRestarts3.Reload();
					AppendAutoRestartGuiLines(automatedRestarts3, list);
				}
				break;
			}
			case "autorestart-skip":
			{
				AutomatedRestartService automatedRestarts2 = module.AutomatedRestarts;
				if (automatedRestarts2 == null)
				{
					flag2 = false;
					message = "Automated restarts are unavailable.";
				}
				else if (!request.Confirmed)
				{
					flag2 = false;
					message = "Confirmation required before skipping the next scheduled restart.";
				}
				else
				{
					message = automatedRestarts2.SkipNext(playerName);
					AppendAutoRestartGuiLines(automatedRestarts2, list);
				}
				break;
			}
			case "autorestart-save":
			{
				AutomatedRestartService automatedRestarts = module.AutomatedRestarts;
				if (automatedRestarts == null)
				{
					flag2 = false;
					message = "Automated restarts are unavailable.";
					break;
				}
				List<string> list3 = SplitCsv(text4);
				List<int> warningMinutes = (from x in SplitCsv(request.Secondary)
					select int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : (-1) into x
					where x > 0
					select x).ToList();
				if (list3.Count == 0)
				{
					flag2 = false;
					message = "Enter at least one UTC restart time (HH:mm or HH:mm:ss).";
					break;
				}
				string restartTimeUtc = list3[0];
				List<string> restartTimesUtc = ((list3.Count > 1) ? list3 : new List<string>());
				(flag2, message, _) = automatedRestarts.SaveSettings(new AutomatedRestartSettingsRequest
				{
					Enabled = automatedRestarts.Config.Enabled,
					RestartTimeUtc = restartTimeUtc,
					RestartTimesUtc = restartTimesUtc,
					WarningMinutes = warningMinutes,
					AnnounceInGame = ((request.IntValue & 1) != 0),
					AnnounceToDiscord = ((request.IntValue & 2) != 0),
					Reason = text5
				});
				AppendAutoRestartGuiLines(automatedRestarts, list);
				break;
			}
			case "chatdisplay-status":
			{
				ChatDisplayService chatDisplay2 = module.ChatDisplay;
				if (chatDisplay2 == null)
				{
					flag2 = false;
					message = "Chat display service is unavailable.";
				}
				else
				{
					message = "Chat display status loaded.";
					AppendChatDisplayGuiLines(chatDisplay2, list);
				}
				break;
			}
			case "chatdisplay-save":
			{
				ChatDisplayService chatDisplay = module.ChatDisplay;
				if (chatDisplay == null)
				{
					flag2 = false;
					message = "Chat display service is unavailable.";
					break;
				}
				(flag2, message, _) = chatDisplay.SaveSettings(new ChatDisplaySettingsRequest
				{
					ShowRoleBeforeName = ((request.IntValue & 1) != 0),
					BoldPlayerName = ((request.IntValue & 8) != 0),
					ShowTimestampBeforeMessage = ((request.IntValue & 2) != 0),
					UseUtcTimestamp = ((request.IntValue & 4) != 0),
					FormatGeneralChat = ((request.IntValue & 0x10) != 0),
					FormatOtherChatGroups = ((request.IntValue & 0x20) != 0),
					AdditionalFormattedChatGroupIds = (from x in SplitCsv(request.Secondary)
						select int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : (-1) into id
						where id >= 0
						select id).Distinct().ToList(),
					TimestampFormat = (string.IsNullOrWhiteSpace(text4) ? "HH:mm" : text4)
				});
				AppendChatDisplayGuiLines(chatDisplay, list);
				break;
			}
			case "undo":
				message = admin.Undo(playerName, "shiftl");
				break;
			case "reports-open":
			case "reports-all":
			{
				ReportsService reports4 = module.Reports;
				if (reports4 == null)
				{
					flag2 = false;
					message = "Reports feature is disabled.";
					break;
				}
				PlayerReportSnapshot snapshot = reports4.GetSnapshot((text == "reports-open") ? "open" : "all", 100);
				message = $"Reports: {snapshot.OpenCount} open • {snapshot.InvestigatingCount} investigating • {snapshot.ResolvedCount} resolved.";
				foreach (PlayerReportRecord item7 in snapshot.Reports.Take(100))
				{
					list.Add($"#{item7.Id} • {item7.Status} • by {item7.ReporterName} • {item7.Reason} • assigned {((!string.IsNullOrWhiteSpace(item7.AssignedStaff)) ? item7.AssignedStaff : "nobody")}");
				}
				break;
			}
			case "report-status":
			{
				ReportsService reports3 = module.Reports;
				if (reports3 == null)
				{
					flag2 = false;
					message = "Reports feature is disabled.";
				}
				else
				{
					message = reports3.SetStatus(request.LongValue, text4, playerName, text5);
				}
				break;
			}
			case "report-assign":
			{
				ReportsService reports2 = module.Reports;
				if (reports2 == null)
				{
					flag2 = false;
					message = "Reports feature is disabled.";
				}
				else
				{
					message = reports2.Assign(request.LongValue, string.IsNullOrWhiteSpace(text4) ? playerName : text4, playerName);
				}
				break;
			}
			case "report-note":
			{
				ReportsService reports = module.Reports;
				if (reports == null)
				{
					flag2 = false;
					message = "Reports feature is disabled.";
				}
				else
				{
					message = reports.AddNote(request.LongValue, playerName, text5);
				}
				break;
			}
			default:
				flag2 = false;
				message = "Unknown admin action.";
				break;
			}
		}
		catch (Exception ex)
		{
			flag2 = false;
			message = "Admin action failed: " + ex.Message;
			api.Logger.Warning("[ServerEssentials/AdminGUI] {0} failed for {1}: {2}", text, player.PlayerName, ex);
		}
		if (flag2 && LooksLikeAdminFailure(message))
		{
			flag2 = false;
		}
		bool flag3 = flag2;
		bool flag4 = flag3;
		if (flag4)
		{
			bool flag5;
			switch (text)
			{
			case "invgive":
			case "invgive-selected":
			case "invqty":
			case "invreplace":
			case "invremove":
			case "invrestore":
				flag5 = true;
				break;
			default:
				flag5 = false;
				break;
			}
			flag4 = flag5;
		}
		if (flag4)
		{
			AppendAdminInventoryGuiLines(admin, text3, list);
		}
		SendAdminCommandGui(player, request.RequestId, flag2, text, message, list);
	}

	private static void AppendAutoRestartGuiLines(AutomatedRestartService restarts, List<string> lines)
	{
		AutomatedRestartSnapshot snapshot = restarts.GetSnapshot();
		lines.Add($"AUTORESTART\t{(snapshot.Enabled ? 1 : 0)}\t{GuiField(string.Join(",", snapshot.EffectiveRestartTimesUtc))}\t{GuiField(string.Join(",", snapshot.WarningMinutes))}\t{GuiField(snapshot.Reason)}\t{(snapshot.AnnounceInGame ? 1 : 0)}\t{(snapshot.AnnounceToDiscord ? 1 : 0)}\t{snapshot.NextRestartUnixMilliseconds}");
		lines.Add("Root /restarttime provider: " + GuiField(snapshot.RootRestartTimeOwner) + " • Fallback: " + snapshot.FallbackCommand);
		if (!string.IsNullOrWhiteSpace(snapshot.NextRestartUtc))
		{
			lines.Add("Next UTC restart: " + snapshot.NextRestartUtc);
		}
	}

	private static void AppendChatDisplayGuiLines(ChatDisplayService chat, List<string> lines)
	{
		ChatDisplaySnapshot snapshot = chat.GetSnapshot();
		lines.Add($"CHATDISPLAY\t{(snapshot.ShowRoleBeforeName ? 1 : 0)}\t{(snapshot.BoldPlayerName ? 1 : 0)}\t{(snapshot.ShowTimestampBeforeMessage ? 1 : 0)}\t{(snapshot.UseUtcTimestamp ? 1 : 0)}\t{GuiField(snapshot.TimestampFormat)}\t{GuiField(snapshot.Preview)}\t{(snapshot.FormatGeneralChat ? 1 : 0)}\t{(snapshot.FormatOtherChatGroups ? 1 : 0)}\t{GuiField(string.Join(",", snapshot.AdditionalFormattedChatGroupIds))}");
	}

	private static List<string> SplitCsv(string? value)
	{
		return (from x in (value ?? "").Split(new char[2] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			where !string.IsNullOrWhiteSpace(x)
			select x).ToList();
	}

	private static void AppendAdminInventoryGuiLines(AdminService admin, string target, List<string> lines)
	{
		if (admin == null || lines == null || string.IsNullOrWhiteSpace(target))
		{
			return;
		}
		foreach (AdminInventorySlotView item in admin.GetInventory(target))
		{
			lines.Add($"INV\t{GuiField(item.Inventory)}\t{GuiField(item.Section)}\t{item.Slot}\t{(item.Empty ? 1 : 0)}\t{item.StackSize}\t{item.MaxStackSize}\t{GuiField(item.Code)}\t{GuiField(item.Name)}\t{item.Durability}\t{item.MaxDurability}\t{item.Temperature.ToString(CultureInfo.InvariantCulture)}\t{GuiField(item.StorageType)}\t{GuiField(item.Fingerprint)}\t{GuiField(item.SerializedStack)}");
		}
	}

	private static string AdminPrivilegeForOperation(string op)
	{
		if (1 == 0)
		{
		}
		string result;
		switch (op)
		{
		case "player-info":
		case "online":
		case "seen":
		case "playtime":
		case "afk":
		case "history":
			result = "nse.player.view";
			break;
		case "warnings":
		case "warn":
		case "unwarn":
			result = "nse.moderation.warn";
			break;
		case "notes":
		case "note-add":
		case "note-remove":
		case "heal":
		case "feed":
		case "revive":
		case "ghost":
		case "freeze":
		case "unfreeze":
		case "kick":
			result = "nse.player.manage";
			break;
		case "smite":
			result = "nse.smite";
			break;
		case "setjail":
		case "jail":
		case "unjail":
		case "jailinfo":
			result = "nse.jail";
			break;
		case "mute":
		case "unmute":
		case "muteinfo":
			result = "nse.moderation.mute";
			break;
		case "tempban":
		case "unban":
		case "baninfo":
			result = "nse.moderation.ban";
			break;
		case "goto":
		case "bring":
		case "back":
		case "sendback":
		case "map-teleport":
			result = "nse.teleport";
			break;
		case "invsee":
		case "itemsearch":
			result = "nse.inventory.view";
			break;
		case "invsnapshot":
		case "invgive":
		case "invgive-selected":
		case "invqty":
		case "invreplace":
		case "invremove":
		case "invrestore":
			result = "nse.inventory.edit";
			break;
		case "laghere":
			result = "nse.diagnostics";
			break;
		case "cleanup-scan":
		case "cleanup-drops":
			result = "nse.cleanup";
			break;
		case "maintenance-on":
		case "maintenance-off":
		case "maintenance-status":
		case "shutdown":
		case "restart":
		case "shutdown-cancel":
		case "autorestart-status":
		case "autorestart-enable":
		case "autorestart-disable":
		case "autorestart-reload":
		case "autorestart-skip":
		case "autorestart-save":
		case "chatdisplay-status":
		case "chatdisplay-save":
			result = "nse.server";
			break;
		case "undo":
			result = "nse.player.manage";
			break;
		case "reports-open":
		case "reports-all":
		case "report-status":
		case "report-assign":
		case "report-note":
			result = "nse.reports.manage";
			break;
		default:
			result = "";
			break;
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private void QueueAdminMapTeleport(IServerPlayer player, AdminCommandGuiRequest request, AdminService admin)
	{
		int x = request.IntValue;
		int z = request.IntValue2;
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (mapTeleportLastRequestMs.TryGetValue(player.PlayerUID, out var value) && num - value < 750)
		{
			SendAdminCommandGui(player, request.RequestId, success: false, "map-teleport", "Map teleport is cooling down for a moment.", new List<string>());
			return;
		}
		mapTeleportLastRequestMs[player.PlayerUID] = num;
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		if (x < 0 || z < 0 || x >= blockAccessor.MapSizeX || z >= blockAccessor.MapSizeZ)
		{
			SendAdminCommandGui(player, request.RequestId, success: false, "map-teleport", "That map position is outside the world bounds.", new List<string>());
			return;
		}
		int dimension = 0;
		if (EntityPositionResolver.TryGetBlockPos(player.Entity, out BlockPos pos))
		{
			dimension = pos.dimension;
		}
		if (dimension != 0)
		{
			SendAdminCommandGui(player, request.RequestId, success: false, "map-teleport", "World-map teleport currently supports the overworld dimension only.", new List<string>());
			return;
		}
		int chunkX = (int)Math.Floor((double)x / 32.0);
		int chunkZ = (int)Math.Floor((double)z / 32.0);
		try
		{
			api.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ, new ChunkLoadOptions
			{
				KeepLoaded = false,
				OnLoaded = () =>
				{
					api.Event.EnqueueMainThreadTask(() =>
					{
						try
						{
							if (player.Entity != null)
							{
								BlockPos target = FindSafeMapTeleportPosition(x, z, dimension);
								string message = admin.MapTeleport(player, target);
								SendAdminCommandGui(player, request.RequestId, success: true, "map-teleport", message, new List<string>());
							}
						}
						catch (Exception ex2)
						{
							api.Logger.Warning("[ServerEssentials/AdminMapTeleport] Completion failed for {0}: {1}", player.PlayerName, ex2);
							SendAdminCommandGui(player, request.RequestId, success: false, "map-teleport", "Map teleport failed while resolving a safe landing position.", new List<string>());
						}
					}, "nses-admin-map-teleport");
				}
			});
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/AdminMapTeleport] Chunk load failed for {0}: {1}", player.PlayerName, ex);
			SendAdminCommandGui(player, request.RequestId, success: false, "map-teleport", "Unable to load the selected map area.", new List<string>());
		}
	}

	private BlockPos FindSafeMapTeleportPosition(int x, int z, int dimension)
	{
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		int num = Math.Max(3, blockAccessor.MapSizeY - 3);
		int num2 = Math.Clamp(blockAccessor.GetRainMapHeightAt(x, z), 1, num - 1);
		int num3 = Math.Clamp(num2 + 1, 1, num);
		for (int i = 0; i <= 24; i++)
		{
			int num4 = num3 + i;
			if (num4 <= num && IsSafeStandingPosition(x, num4, z, dimension))
			{
				return new BlockPos(x, num4, z, dimension);
			}
			if (i != 0)
			{
				int num5 = num3 - i;
				if (num5 >= 1 && IsSafeStandingPosition(x, num5, z, dimension))
				{
					return new BlockPos(x, num5, z, dimension);
				}
			}
		}
		return new BlockPos(x, Math.Clamp(num3 + 1, 1, num), z, dimension);
	}

	private bool IsSafeStandingPosition(int x, int y, int z, int dimension)
	{
		IBlockAccessor blockAccessor = api.World.BlockAccessor;
		BlockPos pos = new BlockPos(x, y, z, dimension);
		BlockPos pos2 = new BlockPos(x, y + 1, z, dimension);
		BlockPos pos3 = new BlockPos(x, y - 1, z, dimension);
		return !blockAccessor.IsNotTraversable(pos) && !blockAccessor.IsNotTraversable(pos2) && blockAccessor.IsNotTraversable(pos3);
	}

	private void SendAdminCommandGui(IServerPlayer player, long requestId, bool success, string operation, string message, List<string> lines)
	{
		channel?.SendPacket(new AdminCommandGuiResponse
		{
			RequestId = requestId,
			Success = success,
			Operation = (operation ?? ""),
			Message = (message ?? ""),
			Lines = (lines ?? new List<string>())
		}, player);
	}

	private static bool LooksLikeAdminFailure(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return false;
		}
		string text = message.ToLowerInvariant();
		return text.Contains("not found") || text.Contains("invalid") || text.Contains("unavailable") || text.Contains("failed") || text.Contains("must be online") || text.StartsWith("usage:") || text.Contains("required") || text.Contains("unknown admin action");
	}

	private static string FormatAdminTime(long unixMs)
	{
		return (unixMs <= 0) ? "never" : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("u", CultureInfo.InvariantCulture);
	}

	private static string FormatAdminDuration(long seconds)
	{
		if (seconds < 0)
		{
			seconds = 0L;
		}
		TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
		if (!(timeSpan.TotalDays >= 1.0))
		{
			if (!(timeSpan.TotalHours >= 1.0))
			{
				if (!(timeSpan.TotalMinutes >= 1.0))
				{
					return $"{timeSpan.Seconds}s";
				}
				return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";
			}
			return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
		}
		return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h {timeSpan.Minutes}m";
	}

	private void HandleBlockLoggerSettings(IServerPlayer player, BlockLoggerSettingsRequest request)
	{
		if (channel == null)
		{
			return;
		}
		string text = (request.Operation ?? "get").Trim().ToLowerInvariant();
		if (text == "get")
		{
			if (!player.HasPrivilege("blocklogger"))
			{
				SendBlockLoggerSettings(player, request.RequestId, success: false, "You do not have the blocklogger privilege.");
				return;
			}
		}
		else if (!player.HasPrivilege("controlserver"))
		{
			SendBlockLoggerSettings(player, request.RequestId, success: false, "You need the controlserver privilege to change BlockLogger settings.");
			return;
		}
		try
		{
			BlockLoggerConfig config = module.Config;
			string text2 = (request.SettingKey ?? "").Trim().ToLowerInvariant();
			if (text == "setbool")
			{
				switch (text2)
				{
				case "enabled":
					config.Enabled = request.BoolValue;
					break;
				case "logblockbreak":
					config.LogBlockBreak = request.BoolValue;
					break;
				case "logblockplace":
					config.LogBlockPlace = request.BoolValue;
					break;
				case "logblockuse":
					config.LogBlockUse = request.BoolValue;
					break;
				case "logcontaineraccess":
					config.LogContainerAccess = request.BoolValue;
					break;
				case "logcontaineritemchanges":
					config.LogContainerItemChanges = request.BoolValue;
					break;
				case "logentityinteract":
					config.LogEntityInteract = request.BoolValue;
					break;
				case "logentityspawn":
					config.LogEntitySpawn = request.BoolValue;
					break;
				case "logentitydeath":
					config.LogEntityDeath = request.BoolValue;
					break;
				case "logentitydespawn":
					config.LogEntityDespawn = request.BoolValue;
					break;
				case "aggregateentityspawndespawn":
					config.AggregateEntitySpawnDespawn = request.BoolValue;
					break;
				default:
					SendBlockLoggerSettings(player, request.RequestId, success: false, "Unknown BlockLogger setting '" + request.SettingKey + "'.");
					return;
				}
				module.SaveRuntimeConfig();
			}
			else if (text == "setint")
			{
				switch (text2)
				{
				case "retentiondays":
					config.RetentionDays = request.IntValue;
					break;
				case "flushintervalms":
					config.FlushIntervalMs = request.IntValue;
					break;
				case "maxqueuedevents":
					config.MaxQueuedEvents = request.IntValue;
					break;
				case "emergencyoverflowcapacity":
					config.EmergencyOverflowCapacity = request.IntValue;
					break;
				case "defaultradius":
					config.DefaultRadius = request.IntValue;
					break;
				case "entityradius":
					config.EntityRadius = request.IntValue;
					break;
				case "maxradius":
					config.MaxRadius = request.IntValue;
					break;
				case "defaultdisplaycount":
					config.DefaultDisplayCount = request.IntValue;
					break;
				case "defaultentitydisplaycount":
					config.DefaultEntityDisplayCount = request.IntValue;
					break;
				case "maxdisplaycount":
					config.MaxDisplayCount = request.IntValue;
					break;
				default:
					SendBlockLoggerSettings(player, request.RequestId, success: false, "Unknown BlockLogger setting '" + request.SettingKey + "'.");
					return;
				}
				module.SaveRuntimeConfig();
			}
			else if (text != "get")
			{
				SendBlockLoggerSettings(player, request.RequestId, success: false, "Unknown BlockLogger settings operation '" + request.Operation + "'.");
				return;
			}
			SendBlockLoggerSettings(player, request.RequestId, success: true, (text == "get") ? "BlockLogger settings" : "BlockLogger setting saved.");
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[BlockLogger] GUI settings request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			SendBlockLoggerSettings(player, request.RequestId, success: false, "BlockLogger settings update failed. Check the server log.");
		}
	}

	private void SendBlockLoggerSettings(IServerPlayer player, long requestId, bool success, string message)
	{
		if (channel != null)
		{
			BlockLoggerConfig config = module.Config;
			channel.SendPacket(new BlockLoggerSettingsResponse
			{
				RequestId = requestId,
				Success = success,
				Message = message,
				Enabled = config.Enabled,
				LogBlockBreak = config.LogBlockBreak,
				LogBlockPlace = config.LogBlockPlace,
				LogBlockUse = config.LogBlockUse,
				LogContainerAccess = config.LogContainerAccess,
				LogContainerItemChanges = config.LogContainerItemChanges,
				LogEntityInteract = config.LogEntityInteract,
				LogEntitySpawn = config.LogEntitySpawn,
				LogEntityDeath = config.LogEntityDeath,
				LogEntityDespawn = config.LogEntityDespawn,
				AggregateEntitySpawnDespawn = config.AggregateEntitySpawnDespawn,
				RetentionDays = config.RetentionDays,
				FlushIntervalMs = config.FlushIntervalMs,
				MaxQueuedEvents = config.MaxQueuedEvents,
				DefaultRadius = config.DefaultRadius,
				EntityRadius = config.EntityRadius,
				MaxRadius = config.MaxRadius,
				DefaultDisplayCount = config.DefaultDisplayCount,
				DefaultEntityDisplayCount = config.DefaultEntityDisplayCount,
				MaxDisplayCount = config.MaxDisplayCount,
				EmergencyOverflowCapacity = config.EmergencyOverflowCapacity
			}, player);
		}
	}

	private void HandleAnimalLimiter(IServerPlayer player, AnimalLimiterRequest request)
	{
		if (channel == null)
		{
			return;
		}
		if (!player.HasPrivilege("controlserver"))
		{
			channel.SendPacket(new AnimalLimiterResponse
			{
				RequestId = request.RequestId,
				Message = "You need the controlserver privilege to change the animal limiter."
			}, player);
			return;
		}
		AnimalLimiterSystem current = AnimalLimiterSystem.Current;
		if (current == null)
		{
			channel.SendPacket(new AnimalLimiterResponse
			{
				RequestId = request.RequestId,
				Message = "Animal Limiter is not available."
			}, player);
			return;
		}
		try
		{
			string text = (request.Operation ?? "status").Trim().ToLowerInvariant();
			int num = Math.Clamp(request.Limit, 0, 1000000);
			if (1 == 0)
			{
			}
			string text2 = text switch
			{
				"target" => current.SetTargetRule(player, num, request.RadiusChunks), 
				"set" => current.SetRuleFromGui(request.Pattern, num, request.RadiusChunks), 
				"remove" => current.RemoveRuleFromGui(request.Pattern), 
				"preview" => current.PreviewFromGui(request.Pattern, string.IsNullOrWhiteSpace(request.Pattern) ? ((int?)null) : new int?(num), request.RadiusChunks), 
				"cull" => current.CullFromGui(), 
				"setradius" => current.SetRadiusFromGui(request.RadiusChunks), 
				"setint" => current.SetIntegerSettingFromGui(request.SettingKey, request.IntValue), 
				"setbool" => current.SetBooleanSettingFromGui(request.SettingKey, request.BoolValue), 
				"history" => "Animal Limiter removal logs", 
				"population" => "Animal Limiter population status", 
				"rules" => "Animal Limiter rules", 
				"settings" => "Animal Limiter settings", 
				_ => "Animal Limiter status", 
			};
			if (1 == 0)
			{
			}
			string message = text2;
			if (1 == 0)
			{
			}
			List<string> list;
			switch (text)
			{
			case "history":
				list = current.GetGuiRemovalLogLines(75);
				break;
			case "population":
				list = current.GetGuiPopulationLines();
				break;
			case "rules":
				list = current.GetGuiRuleLines();
				break;
			case "settings":
			case "setradius":
			case "setint":
			case "setbool":
				list = current.GetGuiSettingsLines();
				break;
			default:
				list = current.GetGuiStatusLines();
				break;
			}
			if (1 == 0)
			{
			}
			List<string> lines = list;
			channel.SendPacket(new AnimalLimiterResponse
			{
				RequestId = request.RequestId,
				Success = true,
				Message = message,
				Lines = lines,
				Enabled = current.GuiEnabled,
				RadiusChunks = current.GuiRadiusChunks,
				DefaultPerEntityLimit = current.GuiDefaultPerEntityLimit,
				PlayerProtectionRadiusBlocks = current.GuiPlayerProtectionRadiusBlocks,
				ReconciliationSeconds = current.GuiReconciliationSeconds,
				LimitAllEntities = current.GuiLimitAllEntities,
				ProtectPets = current.GuiProtectPets,
				ProtectFarmAnimals = current.GuiProtectFarmAnimals,
				ProtectNamedEntities = current.GuiProtectNamedEntities,
				KeepNearestToPlayers = current.GuiKeepNearestToPlayers,
				LogActions = current.GuiLogActions,
				LogToCsv = current.GuiLogToCsv,
				GenerationFilter = current.GuiGenerationFilter
			}, player);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[AnimalLimiter] GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			channel.SendPacket(new AnimalLimiterResponse
			{
				RequestId = request.RequestId,
				Message = "Animal Limiter operation failed. Check the server log."
			}, player);
		}
	}

	private void HandleAntiCheatGui(IServerPlayer player, AntiCheatGuiRequest request)
	{
		if (channel == null)
		{
			return;
		}
		string text = (request.Operation ?? "overview").Trim().ToLowerInvariant();
		bool flag = text == "rescan";
		bool flag2 = text == "setenabled";
		bool flag3;
		switch (text)
		{
		case "blacklistadd":
		case "blacklistremove":
		case "blacklistresolve":
			flag3 = true;
			break;
		default:
			flag3 = false;
			break;
		}
		bool flag4 = flag3;
		flag3 = ((text == "settingssave" || text == "packetsave") ? true : false);
		bool flag5 = flag3;
		bool flag6 = text == "enforcementsave";
		if (flag | flag2 | flag4 | flag5 | flag6)
		{
			if (!player.HasPrivilege("controlserver"))
			{
				string message;
				if (flag2)
				{
					message = "You need the controlserver privilege to enable or disable Anti-Cheat.";
				}
				else if (flag4)
				{
					message = "You need the controlserver privilege to change the Anti-Cheat mod blacklist.";
				}
				else if (flag5)
				{
					message = "You need the controlserver privilege to change Anti-Cheat settings.";
				}
				else
				{
					message = (flag6 ? "You need the controlserver privilege to change Anti-Cheat enforcement rules." : "You need the controlserver privilege to request an anti-cheat rescan.");
				}
				SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, message, new List<string>(), request.Search);
				return;
			}
		}
		else if (!player.HasPrivilege("blocklogger"))
		{
			SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, "You do not have the blocklogger privilege.", new List<string>(), request.Search);
			return;
		}
		AntiCheatService antiCheat = module.AntiCheat;
		if (antiCheat == null)
		{
			SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, "Anti-cheat service is not available.", new List<string>(), request.Search);
			return;
		}
		try
		{
			if (flag2)
			{
				(bool, string) tuple = antiCheat.SaveModuleSettings(request.BoolValue);
				AntiCheatWebSnapshot snapshot = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, tuple.Item1, "overview", tuple.Item2, BuildAntiCheatOverviewLines(snapshot), "");
				return;
			}
			if (flag)
			{
				string text2 = antiCheat.RequestRescan(request.Search ?? "");
				bool success = text2.StartsWith("Anti-cheat rescan requested", StringComparison.OrdinalIgnoreCase);
				AntiCheatPlayerWebSnapshot playerSnapshot = antiCheat.GetPlayerSnapshot(request.Search ?? "");
				AntiCheatHistoryWebSnapshot history = ((playerSnapshot == null) ? null : antiCheat.GetHistorySnapshot(playerSnapshot.PlayerUid, 12));
				List<string> lines = ((playerSnapshot == null) ? new List<string>() : BuildAntiCheatPlayerLines(playerSnapshot, history));
				SendAntiCheatGuiResponse(player, request.RequestId, success, "player", text2, lines, playerSnapshot?.PlayerName ?? request.Search);
				return;
			}
			switch (text)
			{
			case "overview":
			{
				AntiCheatWebSnapshot snapshot4 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, text, "Anti-Cheat overview", BuildAntiCheatOverviewLines(snapshot4), "");
				return;
			}
			case "players":
			{
				AntiCheatWebSnapshot snapshot8 = antiCheat.GetSnapshot();
				List<string> list2 = (from x in snapshot8.Players.OrderByDescending((AntiCheatPlayerWebSnapshot x) => x.RiskScore).ThenBy((AntiCheatPlayerWebSnapshot x) => x.PlayerName, StringComparer.OrdinalIgnoreCase)
					select $"PLAYER\t{x.RiskScore}\t{RiskRating(x.RiskScore)}\t{GuiField(x.Status)}\t{GuiField(x.PlayerName)}\t{GuiField(x.ClientInstalled ? x.ClientVersion : "missing")}\t{x.FindingCount}\t{x.RuntimeNewAssemblyCount}\t{x.ModCount}\t{x.AssemblyCount}\t{GuiField(x.PlayerUid)}").ToList();
				if (list2.Count == 0)
				{
					list2.Add("No online anti-cheat sessions.");
				}
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, text, $"Online players by risk • {snapshot8.Suspicious} suspicious • {snapshot8.Review} review • {snapshot8.Clean} clean", list2, "");
				return;
			}
			case "reports":
			{
				AntiCheatHistoryWebSnapshot historySnapshot = antiCheat.GetHistorySnapshot(request.Search ?? "", 80);
				List<string> lines2 = BuildAntiCheatReportLines(historySnapshot);
				string text3 = (historySnapshot.PlayerFilter ?? "").Trim();
				string message2 = ((text3.Length == 0) ? $"Persistent reports • {historySnapshot.TotalRecords} total • {historySnapshot.Last24Hours} in last 24h" : $"Persistent reports for {text3} • showing {historySnapshot.Incidents.Count} of {historySnapshot.TotalRecords}");
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, "reports", message2, lines2, "");
				return;
			}
			case "enforcement":
			{
				AntiCheatWebSnapshot snapshot5 = antiCheat.GetSnapshot();
				List<AntiCheatConsequenceRuleGuiPacket> list = BuildAntiCheatConsequencePackets(snapshot5.ConsequenceRules);
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, "enforcement", $"Enforcement rules • {list.Count((AntiCheatConsequenceRuleGuiPacket x) => x.Enabled)} enabled / {list.Count} total", BuildAntiCheatEnforcementLines(list), "", null, list);
				return;
			}
			case "enforcementsave":
			{
				List<AntiCheatConsequenceRule> rules = (request.ConsequenceRules ?? new List<AntiCheatConsequenceRuleGuiPacket>()).Select(ToAntiCheatConsequenceRule).ToList();
				(bool, string) tuple4 = antiCheat.SaveConsequenceRules(rules, request.ApplyCurrent);
				AntiCheatWebSnapshot snapshot10 = antiCheat.GetSnapshot();
				List<AntiCheatConsequenceRuleGuiPacket> list3 = BuildAntiCheatConsequencePackets(snapshot10.ConsequenceRules);
				SendAntiCheatGuiResponse(player, request.RequestId, tuple4.Item1, "enforcement", tuple4.Item2, BuildAntiCheatEnforcementLines(list3), "", null, list3);
				return;
			}
			case "packet":
			{
				AntiCheatWebSnapshot snapshot9 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, "packet", "Packet protection settings", BuildAntiCheatPacketProtectionSummary(snapshot9), "", null, null, BuildAntiCheatPacketProtectionPacket(snapshot9));
				return;
			}
			case "packetsave":
			{
				if (request.PacketSettings == null)
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, "packet", "No packet protection settings were supplied.", new List<string>(), "");
					return;
				}
				AntiCheatPacketProtectionGuiPacket packetSettings = request.PacketSettings;
				(bool, string) tuple3 = antiCheat.SaveGuiPacketProtectionSettings(new AntiCheatPacketProtectionGuiSaveRequest
				{
					PacketGuardEnabled = packetSettings.PacketGuardEnabled,
					PlayerWarningEnabled = packetSettings.PlayerWarningEnabled,
					PlayerWarningCooldownSeconds = packetSettings.PlayerWarningCooldownSeconds,
					AccelerationSampleMinMs = packetSettings.AccelerationSampleMinMs,
					AccelerationSmoothingFactor = packetSettings.AccelerationSmoothingFactor,
					NoclipEnabled = packetSettings.NoclipEnabled,
					NoclipRequiredCollisionSamples = packetSettings.NoclipRequiredCollisionSamples,
					NoclipMinPenetrationDepth = packetSettings.NoclipMinPenetrationDepth,
					NoclipEndpointMinNetMovement = packetSettings.NoclipEndpointMinNetMovement,
					MountAccelerationMinSpeedRatio = packetSettings.MountAccelerationMinSpeedRatio,
					DiagnosticContextEnabled = packetSettings.DiagnosticContextEnabled,
					DiagnosticSuppressionEnabled = packetSettings.DiagnosticSuppressionEnabled,
					DiagnosticSpeedHistorySamples = packetSettings.DiagnosticSpeedHistorySamples,
					MovementEnabled = packetSettings.MovementEnabled,
					MovementSampleMs = packetSettings.MovementSampleMs,
					HardMoveDistance = packetSettings.HardMoveDistance,
					MovementConfirmSamples = packetSettings.MovementConfirmSamples,
					MovementConfirmWindowMs = packetSettings.MovementConfirmWindowMs,
					MovementExtremeMultiplier = packetSettings.MovementExtremeMultiplier,
					CorrectionGraceMs = packetSettings.CorrectionGraceMs,
					RejectUnauthorizedMovement = packetSettings.RejectUnauthorizedMovement,
					ExemptAdmins = packetSettings.ExemptAdmins,
					ExemptMountedPlayers = packetSettings.ExemptMountedPlayers,
					MountGraceMs = packetSettings.MountGraceMs,
					EngineTeleportGraceMs = packetSettings.EngineTeleportGraceMs,
					TPRunesCompatibility = packetSettings.TPRunesCompatibility,
					TPRunesGraceMs = packetSettings.TPRunesGraceMs,
					Th3EssentialsSpawnCompatibility = packetSettings.Th3EssentialsSpawnCompatibility,
					Th3EssentialsSpawnGraceMs = packetSettings.Th3EssentialsSpawnGraceMs,
					ReachEnabled = packetSettings.ReachEnabled,
					ReachTolerance = packetSettings.ReachTolerance,
					ReachLatencyCompensation = packetSettings.ReachLatencyCompensation,
					ReachMaxLatencyCompensation = packetSettings.ReachMaxLatencyCompensation,
					ReachPositionHistoryEnabled = packetSettings.ReachPositionHistoryEnabled,
					ReachPositionHistoryMs = packetSettings.ReachPositionHistoryMs,
					ReachMaxRewindMs = packetSettings.ReachMaxRewindMs,
					ReachUseExtraTolerance = packetSettings.ReachUseExtraTolerance,
					ReachBreakExtraTolerance = packetSettings.ReachBreakExtraTolerance,
					ReachNearThresholdStrikes = packetSettings.ReachNearThresholdStrikes,
					ReachStrikeWindowMs = packetSettings.ReachStrikeWindowMs,
					ReachStrikeDecayMs = packetSettings.ReachStrikeDecayMs,
					ReachHardExcess = packetSettings.ReachHardExcess,
					RejectOutOfReachActions = packetSettings.RejectOutOfReachActions,
					MiningEnabled = packetSettings.MiningEnabled,
					VeinWindowMs = packetSettings.VeinWindowMs,
					ConnectedBreakLimit = packetSettings.ConnectedBreakLimit,
					MiningDuplicateTargetMs = packetSettings.MiningDuplicateTargetMs,
					RejectConnectedBreakBursts = packetSettings.RejectConnectedBreakBursts,
					MiningCadenceValidationEnabled = packetSettings.MiningCadenceValidationEnabled,
					MiningCadenceMinExpectedMs = packetSettings.MiningCadenceMinExpectedMs,
					MiningCadenceSuspiciousRatio = packetSettings.MiningCadenceSuspiciousRatio,
					MiningCadenceConfirmations = packetSettings.MiningCadenceConfirmations,
					MiningCadenceConfirmationWindowMs = packetSettings.MiningCadenceConfirmationWindowMs,
					RejectImpossibleMiningCadence = packetSettings.RejectImpossibleMiningCadence,
					TpsAwarenessEnabled = packetSettings.TpsAwarenessEnabled,
					TpsDegradedPercent = packetSettings.TpsDegradedPercent,
					TpsCriticalPercent = packetSettings.TpsCriticalPercent,
					TpsMaxTimingMultiplier = packetSettings.TpsMaxTimingMultiplier,
					TpsSuppressTimingRejectsWhenCritical = packetSettings.TpsSuppressTimingRejectsWhenCritical,
					PerFamilyRiskEnabled = packetSettings.PerFamilyRiskEnabled,
					MovementRiskDecaySeconds = packetSettings.MovementRiskDecaySeconds,
					MovementRiskReviewThreshold = packetSettings.MovementRiskReviewThreshold,
					MovementRiskSuspiciousThreshold = packetSettings.MovementRiskSuspiciousThreshold,
					ReachRiskDecaySeconds = packetSettings.ReachRiskDecaySeconds,
					ReachRiskReviewThreshold = packetSettings.ReachRiskReviewThreshold,
					ReachRiskSuspiciousThreshold = packetSettings.ReachRiskSuspiciousThreshold,
					MiningRiskDecaySeconds = packetSettings.MiningRiskDecaySeconds,
					MiningRiskReviewThreshold = packetSettings.MiningRiskReviewThreshold,
					MiningRiskSuspiciousThreshold = packetSettings.MiningRiskSuspiciousThreshold,
					PacketAbuseRiskDecaySeconds = packetSettings.PacketAbuseRiskDecaySeconds,
					PacketAbuseRiskReviewThreshold = packetSettings.PacketAbuseRiskReviewThreshold,
					PacketAbuseRiskSuspiciousThreshold = packetSettings.PacketAbuseRiskSuspiciousThreshold,
					ClientIntegrityRiskDecaySeconds = packetSettings.ClientIntegrityRiskDecaySeconds,
					ClientIntegrityRiskReviewThreshold = packetSettings.ClientIntegrityRiskReviewThreshold,
					ClientIntegrityRiskSuspiciousThreshold = packetSettings.ClientIntegrityRiskSuspiciousThreshold,
					IncidentCorrelationEnabled = packetSettings.IncidentCorrelationEnabled,
					IncidentCorrelationWindowSeconds = packetSettings.IncidentCorrelationWindowSeconds,
					IncidentCorrelationRequiredCategories = packetSettings.IncidentCorrelationRequiredCategories,
					IncidentCorrelationMaxRiskBonus = packetSettings.IncidentCorrelationMaxRiskBonus,
					HistoryEnabled = packetSettings.HistoryEnabled,
					HistoryRetentionDays = packetSettings.HistoryRetentionDays,
					HistoryMaxRecords = packetSettings.HistoryMaxRecords
				});
				AntiCheatWebSnapshot snapshot7 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, tuple3.Item1, "packet", tuple3.Item2, BuildAntiCheatPacketProtectionSummary(snapshot7), "", null, null, BuildAntiCheatPacketProtectionPacket(snapshot7));
				return;
			}
			case "settings":
			{
				AntiCheatWebSnapshot snapshot6 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, "settings", "Anti-Cheat settings", BuildAntiCheatSettingsSummary(snapshot6), "", BuildAntiCheatSettingsPacket(snapshot6));
				return;
			}
			case "settingssave":
			{
				if (request.Settings == null)
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, "settings", "No Anti-Cheat settings were supplied.", new List<string>(), "");
					return;
				}
				AntiCheatGuiSettingsPacket settings = request.Settings;
				(bool, string) tuple2 = antiCheat.SaveGuiSettings(new AntiCheatGuiSettingsSaveRequest
				{
					RequireClient = settings.RequireClient,
					RescanMinutes = settings.RescanMinutes,
					RuntimeRescanOnChange = settings.RuntimeRescanOnChange,
					RuntimeChangeRescanCooldownSeconds = settings.RuntimeChangeRescanCooldownSeconds,
					SimilarityDetectionEnabled = settings.SimilarityDetectionEnabled,
					SimilarityReviewThresholdPercent = settings.SimilarityReviewThresholdPercent,
					SimilarityHighThresholdPercent = settings.SimilarityHighThresholdPercent
				});
				AntiCheatWebSnapshot snapshot3 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, tuple2.Item1, "settings", tuple2.Item2, BuildAntiCheatSettingsSummary(snapshot3), "", BuildAntiCheatSettingsPacket(snapshot3));
				return;
			}
			case "blacklist":
			{
				AntiCheatWebSnapshot snapshot2 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, "blacklist", $"Client mod blacklist • {snapshot2.ForbiddenModIds.Count} forbidden mod ID(s)", BuildAntiCheatBlacklistLines(snapshot2), "");
				return;
			}
			case "blacklistadd":
			case "blacklistremove":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			if (flag3)
			{
				string text4 = ((text == "blacklistadd") ? "add" : "remove");
				(bool, string) tuple5 = antiCheat.UpdateForbiddenMod(text4, request.Search ?? "", (text4 == "add") ? (request.Secondary ?? "") : "");
				AntiCheatWebSnapshot snapshot11 = antiCheat.GetSnapshot();
				SendAntiCheatGuiResponse(player, request.RequestId, tuple5.Item1, "blacklist", tuple5.Item2, BuildAntiCheatBlacklistLines(snapshot11), "");
			}
			else if (text == "blacklistresolve")
			{
				string modId = (request.Search ?? "").Trim().ToLowerInvariant();
				if (modId.Length == 0)
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, "blacklistresolve", "Choose a blacklisted mod before resolving fingerprints.", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
					return;
				}
				if (!antiCheat.Config.ForbiddenModIds.Any((string x) => string.Equals(x?.Trim(), modId, StringComparison.OrdinalIgnoreCase)))
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, "blacklistresolve", "Add the mod ID to the blacklist before resolving its Mod DB fingerprint.", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
					return;
				}
				if (!fingerprintResolutionGate.Wait(0))
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, "blacklistresolve", "Another Mod DB fingerprint resolution is already running. Wait for it to finish before starting the next one.", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
					return;
				}
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, "blacklistresolve", "Resolving official Mod DB fingerprints for " + modId + "...", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
				ResolveAntiCheatBlacklistFingerprintAsync(player, request.RequestId, modId);
			}
			else if (text == "player")
			{
				string text5 = (request.Search ?? "").Trim();
				if (text5.Length == 0)
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, "Enter a player name or UID.", new List<string>(), text5);
					return;
				}
				AntiCheatPlayerWebSnapshot playerSnapshot2 = antiCheat.GetPlayerSnapshot(text5);
				if (playerSnapshot2 == null)
				{
					SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, "Player anti-cheat session was not found.", new List<string>(), text5);
					return;
				}
				AntiCheatHistoryWebSnapshot historySnapshot2 = antiCheat.GetHistorySnapshot(playerSnapshot2.PlayerUid, 12);
				SendAntiCheatGuiResponse(player, request.RequestId, success: true, text, $"{playerSnapshot2.PlayerName} • risk {playerSnapshot2.RiskScore}/100 • {RiskRating(playerSnapshot2.RiskScore)}", BuildAntiCheatPlayerLines(playerSnapshot2, historySnapshot2), playerSnapshot2.PlayerName);
			}
			else
			{
				SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, "Unknown anti-cheat GUI operation '" + request.Operation + "'.", new List<string>(), request.Search);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[AntiCheat] GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			SendAntiCheatGuiResponse(player, request.RequestId, success: false, text, "Anti-cheat GUI request failed. Check the server log.", new List<string>(), request.Search);
		}
	}

	private async Task ResolveAntiCheatBlacklistFingerprintAsync(IServerPlayer player, long requestId, string modId)
	{
		AntiCheatModDbFingerprintResolution resolution;
		try
		{
			resolution = await AntiCheatModDbFingerprintResolver.ResolveAsync(modId, "1.22.7").ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			resolution = new AntiCheatModDbFingerprintResolution
			{
				Success = false,
				ModId = modId,
				Message = "Could not resolve Mod DB fingerprints: " + ex2.GetType().Name + ": " + ex2.Message
			};
		}
		try
		{
			api.Event.EnqueueMainThreadTask(() =>
			{
				try
				{
					AntiCheatService antiCheat = module.AntiCheat;
					if (antiCheat == null)
					{
						SendAntiCheatGuiResponse(player, requestId, success: false, "blacklistresolve", "Anti-cheat service is no longer available.", new List<string>(), "");
					}
					else if (!resolution.Success)
					{
						SendAntiCheatGuiResponse(player, requestId, success: false, "blacklistresolve", resolution.Message, BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
					}
					else
					{
						(bool, string) tuple = antiCheat.ApplyResolvedForbiddenFingerprint(resolution);
						AntiCheatWebSnapshot snapshot = antiCheat.GetSnapshot();
						string message = (tuple.Item1 ? $"{tuple.Item2} Resolved {resolution.Releases.Count} release fingerprint{((resolution.Releases.Count == 1) ? "" : "s")}." : tuple.Item2);
						SendAntiCheatGuiResponse(player, requestId, tuple.Item1, "blacklistresolve", message, BuildAntiCheatBlacklistLines(snapshot), "");
					}
				}
				finally
				{
					fingerprintResolutionGate.Release();
				}
			}, "anticheat-gui-fingerprint-resolve");
		}
		catch (Exception ex)
		{
			Exception ex3 = ex;
			fingerprintResolutionGate.Release();
			api.Logger.Warning("[AntiCheat] Could not queue GUI fingerprint result for {0}: {1}: {2}", modId, ex3.GetType().Name, ex3.Message);
		}
	}

	private static List<string> BuildAntiCheatOverviewLines(AntiCheatWebSnapshot snapshot)
	{
		int value = snapshot.Players.Count((AntiCheatPlayerWebSnapshot x) => x.RiskScore >= 50);
		int value2 = snapshot.Players.Count((AntiCheatPlayerWebSnapshot x) => x.RiskScore >= 25 && x.RiskScore < 50);
		int value3 = snapshot.Players.Count((AntiCheatPlayerWebSnapshot x) => x.RiskScore >= 10 && x.RiskScore < 25);
		return new List<string>
		{
			$"Enabled: {snapshot.Enabled}",
			$"Runtime available: {snapshot.Available}",
			"Runtime status: " + (string.IsNullOrWhiteSpace(snapshot.AvailabilityReason) ? "No detail available" : snapshot.AvailabilityReason),
			"Network channel: " + (string.IsNullOrWhiteSpace(snapshot.NetworkChannelName) ? "Unavailable" : snapshot.NetworkChannelName),
			$"Excess integrity packets dropped: {snapshot.NetworkPacketsDropped}",
			$"Server add-on installed: {snapshot.ServerCompanionInstalled}",
			"Server add-on version: " + (string.IsNullOrWhiteSpace(snapshot.ServerCompanionVersion) ? "not installed" : snapshot.ServerCompanionVersion),
			"Expected companion version: " + snapshot.ExpectedClientVersion,
			$"Protocol: v{snapshot.ProtocolVersion}",
			$"Client required by policy: {snapshot.RequireClient}",
			$"Tracked online players: {snapshot.TrackedPlayers}",
			$"Integrity clients connected: {snapshot.ClientsConnected}/{snapshot.OnlinePlayers}",
			$"Clean: {snapshot.Clean}",
			$"Review: {snapshot.Review}",
			$"Suspicious: {snapshot.Suspicious}",
			$"High/Critical risk: {value}",
			$"Elevated risk: {value2}",
			$"Guarded risk: {value3}",
			$"Awaiting/scanning: {snapshot.AwaitingOrScanning}",
			$"Missing/timed out: {snapshot.MissingOrTimedOut}",
			$"Blacklisted client mods: {snapshot.ForbiddenModIds.Count}",
			$"Persistent reports: {snapshot.History.TotalRecords} total / {snapshot.History.Last24Hours} last 24h",
			$"Players with stored reports: {snapshot.History.UniquePlayers}",
			$"Periodic rescan: every {snapshot.RescanMinutes} minute(s)",
			"Runtime change rescan: " + (snapshot.RuntimeRescanOnChange ? "Enabled" : "Disabled"),
			$"Runtime rescan cooldown: {snapshot.RuntimeChangeRescanCooldownSeconds}s"
		};
	}

	private static List<string> BuildAntiCheatPlayerLines(AntiCheatPlayerWebSnapshot detail, AntiCheatHistoryWebSnapshot? history = null)
	{
		List<string> list = new List<string>
		{
			"Player: " + detail.PlayerName,
			"UID: " + detail.PlayerUid,
			"Status: " + detail.Status,
			$"Risk score: {detail.RiskScore}/100",
			"Risk rating: " + RiskRating(detail.RiskScore),
			$"Client installed: {detail.ClientInstalled}",
			"Client version: " + detail.ClientVersion,
			"Game version: " + detail.GameVersion,
			"Platform: " + detail.Platform,
			$"Excess integrity packets dropped: {detail.NetworkPacketsDropped}",
			$"Mods / assemblies: {detail.ModCount} / {detail.AssemblyCount}",
			"Client DLL SHA-256: " + ShortHash(detail.ClientAssemblySha256),
			"Client package SHA-256: " + ShortHash(detail.ClientPackageSha256),
			"Client MVID: " + detail.ClientAssemblyMvid,
			$"Runtime generation: {detail.RuntimeGeneration}",
			$"Runtime assemblies: baseline {detail.RuntimeBaselineAssemblyCount}, loaded {detail.RuntimeLoadedAssemblyCount}, dynamic {detail.RuntimeDynamicAssemblyCount}",
			$"Post-baseline assemblies: {detail.RuntimeNewAssemblyCount} ({detail.RuntimeUnassociatedNewAssemblyCount} unassociated)",
			"Runtime digest: " + ShortHash(detail.RuntimeDigest),
			$"Findings: {detail.FindingCount}",
			"Last scan: " + FormatUtc(detail.LastScanUtc),
			"Last heartbeat: " + FormatUtc(detail.LastHeartbeatUtc),
			"Last consequence: " + (string.IsNullOrWhiteSpace(detail.LastConsequence) ? "None" : detail.LastConsequence)
		};
		foreach (AntiCheatFindingWebSnapshot item in (from x in detail.Findings
			orderby x.ConfidenceScore descending, x.Severity descending
			select x).Take(100))
		{
			list.Add($"FINDING\t{item.Severity}\t{GuiField(item.Confidence)}\t{item.ConfidenceScore}\t{GuiField(item.Category)}\t{GuiField(item.Code)}\t{GuiField(item.Summary)}\t{GuiField(item.Subject)}\t{GuiField(item.RecommendedAction)}");
		}
		if (history != null)
		{
			list.Add($"Recent stored reports shown: {history.Incidents.Count}");
			foreach (AntiCheatIncidentWebSnapshot item2 in history.Incidents.Take(8))
			{
				list.Add(BuildAntiCheatReportLine(item2));
			}
		}
		foreach (AntiCheatModWebSnapshot item3 in detail.Mods.Take(12))
		{
			list.Add($"MOD\t{GuiField(item3.ModId)}\t{GuiField(item3.Version)}\t{GuiField(item3.ModType)}\t{GuiField(item3.FileName)}");
		}
		foreach (AntiCheatRuntimeAssemblyWebSnapshot item4 in detail.RuntimeNewAssemblies.Take(12))
		{
			list.Add($"Runtime + {item4.AssemblyName} {item4.Version} | {(item4.IsDynamic ? "dynamic" : item4.FileName)} | ctx {item4.LoadContextName} | mod-associated {item4.AssociatedWithLoadedMod}");
		}
		foreach (AntiCheatScannerMetricWebSnapshot scannerMetric in detail.ScannerMetrics)
		{
			list.Add($"Scanner {scannerMetric.Scanner}: {(scannerMetric.Success ? "OK" : "FAILED")} | {scannerMetric.DurationMs}ms | mods {scannerMetric.ModCount} | asm {scannerMetric.AssemblyCount} | findings {scannerMetric.FindingCount} | cache {scannerMetric.CacheHits}/{scannerMetric.CacheMisses}");
		}
		return list;
	}

	private static List<string> BuildAntiCheatReportLines(AntiCheatHistoryWebSnapshot history)
	{
		List<string> list = new List<string>();
		foreach (AntiCheatIncidentWebSnapshot incident in history.Incidents)
		{
			list.Add(BuildAntiCheatReportLine(incident));
		}
		if (list.Count == 0)
		{
			list.Add(history.Enabled ? "No persistent Anti-Cheat reports match this filter." : "Persistent Anti-Cheat history is disabled.");
		}
		return list;
	}

	private static string BuildAntiCheatReportLine(AntiCheatIncidentWebSnapshot incident)
	{
		string value = FormatUtc(incident.UnixMilliseconds);
		return $"REPORT\t{GuiField(value)}\t{GuiField(incident.PlayerName)}\t{incident.RiskBefore}\t{incident.RiskAfter}\t{RiskRating(incident.RiskAfter)}\t{GuiField(incident.Confidence)}\t{incident.ConfidenceScore}\t{incident.Severity}\t{GuiField(incident.Category)}\t{GuiField(incident.Code)}\t{GuiField(incident.Summary)}\t{GuiField(incident.RecommendedAction)}";
	}

	private static List<AntiCheatConsequenceRuleGuiPacket> BuildAntiCheatConsequencePackets(IEnumerable<AntiCheatConsequenceRuleWebSnapshot>? rules)
	{
		return (rules ?? Enumerable.Empty<AntiCheatConsequenceRuleWebSnapshot>()).Select((AntiCheatConsequenceRuleWebSnapshot rule) => new AntiCheatConsequenceRuleGuiPacket
		{
			Id = (rule.Id ?? ""),
			Name = (rule.Name ?? ""),
			Enabled = rule.Enabled,
			TriggerType = (rule.TriggerType ?? "FindingCode"),
			TriggerValue = (rule.TriggerValue ?? ""),
			Subject = (rule.Subject ?? ""),
			Action = (rule.Action ?? "Log"),
			Duration = (rule.Duration ?? "30m"),
			Message = (rule.Message ?? "")
		}).ToList();
	}

	private static AntiCheatConsequenceRule ToAntiCheatConsequenceRule(AntiCheatConsequenceRuleGuiPacket rule)
	{
		return new AntiCheatConsequenceRule
		{
			Id = (string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim()),
			Name = (rule.Name ?? "").Trim(),
			Enabled = rule.Enabled,
			TriggerType = (rule.TriggerType ?? "FindingCode").Trim(),
			TriggerValue = (rule.TriggerValue ?? "").Trim(),
			Subject = (rule.Subject ?? "").Trim(),
			Action = (rule.Action ?? "Log").Trim(),
			Duration = (rule.Duration ?? "").Trim(),
			Message = (rule.Message ?? "").Trim()
		};
	}

	private static List<string> BuildAntiCheatEnforcementLines(IEnumerable<AntiCheatConsequenceRuleGuiPacket>? rules)
	{
		List<AntiCheatConsequenceRuleGuiPacket> list = (rules ?? Enumerable.Empty<AntiCheatConsequenceRuleGuiPacket>()).ToList();
		List<string> list2 = new List<string>();
		for (int i = 0; i < list.Count; i++)
		{
			AntiCheatConsequenceRuleGuiPacket antiCheatConsequenceRuleGuiPacket = list[i];
			list2.Add($"RULE\t{i}\t{GuiField(antiCheatConsequenceRuleGuiPacket.Id)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.Name)}\t{(antiCheatConsequenceRuleGuiPacket.Enabled ? 1 : 0)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.TriggerType)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.TriggerValue)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.Subject)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.Action)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.Duration)}\t{GuiField(antiCheatConsequenceRuleGuiPacket.Message)}");
		}
		if (list2.Count == 0)
		{
			list2.Add("No automatic enforcement rules are configured. Detection and reporting still continue normally.");
		}
		return list2;
	}

	private static List<string> BuildAntiCheatBlacklistLines(AntiCheatWebSnapshot snapshot)
	{
		Dictionary<string, AntiCheatForbiddenFingerprintWebSnapshot> dictionary = snapshot.ForbiddenFingerprints.Where((AntiCheatForbiddenFingerprintWebSnapshot x) => !string.IsNullOrWhiteSpace(x.ModId)).GroupBy((AntiCheatForbiddenFingerprintWebSnapshot x) => x.ModId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, AntiCheatForbiddenFingerprintWebSnapshot> g) => g.Key, (IGrouping<string, AntiCheatForbiddenFingerprintWebSnapshot> g) => g.First(), StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>();
		foreach (string item in snapshot.ForbiddenModIds.OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase))
		{
			dictionary.TryGetValue(item, out var value);
			string value2 = value?.Source ?? "ID policy";
			int value3 = (value?.AssemblyHashCount ?? 0) + (value?.AssemblyStructureHashCount ?? 0) + (value?.AssemblyBehaviorHashCount ?? 0);
			string value4 = ((value != null && value.HasPackageHash) ? "package" : "no-package");
			string value5 = ((value != null && value.HasContentHash) ? "content" : "no-content");
			list.Add($"BLACKLIST\t{GuiField(item)}\t{GuiField(value2)}\t{value4}\t{value5}\t{value3}\t{value?.ManualEntryCount ?? 0}\t{(value?.ResolvedVersions?.Count).GetValueOrDefault()}\t{GuiField(value?.ResolvedVersion)}\t{GuiField(FormatUtc(value?.ResolvedUtc ?? 0))}");
		}
		if (list.Count == 0)
		{
			list.Add("No client mod IDs are currently blacklisted.");
		}
		return list;
	}

	private static string GuiField(string? value)
	{
		return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
	}

	private static string RiskRating(int risk)
	{
		risk = Math.Clamp(risk, 0, 100);
		if (risk >= 75)
		{
			return "Critical";
		}
		if (risk >= 50)
		{
			return "High";
		}
		if (risk >= 25)
		{
			return "Elevated";
		}
		if (risk >= 10)
		{
			return "Guarded";
		}
		return "Low";
	}

	private static AntiCheatPacketProtectionGuiPacket BuildAntiCheatPacketProtectionPacket(AntiCheatWebSnapshot snapshot)
	{
		AntiCheatPacketGuardWebSnapshot antiCheatPacketGuardWebSnapshot = snapshot.PacketGuard ?? new AntiCheatPacketGuardWebSnapshot();
		AntiCheatHistorySummaryWebSnapshot antiCheatHistorySummaryWebSnapshot = snapshot.History ?? new AntiCheatHistorySummaryWebSnapshot();
		return new AntiCheatPacketProtectionGuiPacket
		{
			PacketGuardEnabled = antiCheatPacketGuardWebSnapshot.Enabled,
			IngressEnabled = antiCheatPacketGuardWebSnapshot.IngressEnabled,
			IngressActive = antiCheatPacketGuardWebSnapshot.IngressActive,
			IngressStatus = antiCheatPacketGuardWebSnapshot.IngressStatus,
			PlayerWarningEnabled = antiCheatPacketGuardWebSnapshot.PlayerWarningEnabled,
			PlayerWarningCooldownSeconds = antiCheatPacketGuardWebSnapshot.PlayerWarningCooldownSeconds,
			AccelerationSampleMinMs = antiCheatPacketGuardWebSnapshot.AccelerationSampleMinMs,
			AccelerationSmoothingFactor = antiCheatPacketGuardWebSnapshot.AccelerationSmoothingFactor,
			NoclipEnabled = antiCheatPacketGuardWebSnapshot.NoclipEnabled,
			NoclipRequiredCollisionSamples = antiCheatPacketGuardWebSnapshot.NoclipRequiredCollisionSamples,
			NoclipMinPenetrationDepth = antiCheatPacketGuardWebSnapshot.NoclipMinPenetrationDepth,
			NoclipEndpointMinNetMovement = antiCheatPacketGuardWebSnapshot.NoclipEndpointMinNetMovement,
			MountAccelerationMinSpeedRatio = antiCheatPacketGuardWebSnapshot.MountAccelerationMinSpeedRatio,
			DiagnosticContextEnabled = antiCheatPacketGuardWebSnapshot.DiagnosticContextEnabled,
			DiagnosticSuppressionEnabled = antiCheatPacketGuardWebSnapshot.DiagnosticSuppressionEnabled,
			DiagnosticSpeedHistorySamples = antiCheatPacketGuardWebSnapshot.DiagnosticSpeedHistorySamples,
			MovementEnabled = antiCheatPacketGuardWebSnapshot.MovementEnabled,
			MovementSampleMs = antiCheatPacketGuardWebSnapshot.MovementSampleMs,
			HardMoveDistance = antiCheatPacketGuardWebSnapshot.HardMoveDistance,
			MovementConfirmSamples = antiCheatPacketGuardWebSnapshot.MovementConfirmSamples,
			MovementConfirmWindowMs = antiCheatPacketGuardWebSnapshot.MovementConfirmWindowMs,
			MovementExtremeMultiplier = antiCheatPacketGuardWebSnapshot.MovementExtremeMultiplier,
			CorrectionGraceMs = antiCheatPacketGuardWebSnapshot.CorrectionGraceMs,
			RejectUnauthorizedMovement = antiCheatPacketGuardWebSnapshot.RejectUnauthorizedMovement,
			ExemptAdmins = antiCheatPacketGuardWebSnapshot.ExemptAdmins,
			ExemptMountedPlayers = antiCheatPacketGuardWebSnapshot.ExemptMountedPlayers,
			MountGraceMs = antiCheatPacketGuardWebSnapshot.MountGraceMs,
			EngineTeleportGraceMs = antiCheatPacketGuardWebSnapshot.EngineTeleportGraceMs,
			TPRunesCompatibility = antiCheatPacketGuardWebSnapshot.TPRunesCompatibility,
			TPRunesGraceMs = antiCheatPacketGuardWebSnapshot.TPRunesGraceMs,
			TPRunesDetected = antiCheatPacketGuardWebSnapshot.TPRunesDetected,
			Th3EssentialsSpawnCompatibility = antiCheatPacketGuardWebSnapshot.Th3EssentialsSpawnCompatibility,
			Th3EssentialsSpawnGraceMs = antiCheatPacketGuardWebSnapshot.Th3EssentialsSpawnGraceMs,
			Th3EssentialsDetected = antiCheatPacketGuardWebSnapshot.Th3EssentialsDetected,
			ReachEnabled = antiCheatPacketGuardWebSnapshot.ReachEnabled,
			ReachTolerance = antiCheatPacketGuardWebSnapshot.ReachTolerance,
			ReachLatencyCompensation = antiCheatPacketGuardWebSnapshot.ReachLatencyCompensation,
			ReachMaxLatencyCompensation = antiCheatPacketGuardWebSnapshot.ReachMaxLatencyCompensation,
			ReachPositionHistoryEnabled = antiCheatPacketGuardWebSnapshot.ReachPositionHistoryEnabled,
			ReachPositionHistoryMs = antiCheatPacketGuardWebSnapshot.ReachPositionHistoryMs,
			ReachMaxRewindMs = antiCheatPacketGuardWebSnapshot.ReachMaxRewindMs,
			ReachUseExtraTolerance = antiCheatPacketGuardWebSnapshot.ReachUseExtraTolerance,
			ReachBreakExtraTolerance = antiCheatPacketGuardWebSnapshot.ReachBreakExtraTolerance,
			ReachNearThresholdStrikes = antiCheatPacketGuardWebSnapshot.ReachNearThresholdStrikes,
			ReachStrikeWindowMs = antiCheatPacketGuardWebSnapshot.ReachStrikeWindowMs,
			ReachStrikeDecayMs = antiCheatPacketGuardWebSnapshot.ReachStrikeDecayMs,
			ReachHardExcess = antiCheatPacketGuardWebSnapshot.ReachHardExcess,
			RejectOutOfReachActions = antiCheatPacketGuardWebSnapshot.RejectOutOfReachActions,
			MiningEnabled = antiCheatPacketGuardWebSnapshot.MiningEnabled,
			VeinWindowMs = antiCheatPacketGuardWebSnapshot.VeinWindowMs,
			ConnectedBreakLimit = antiCheatPacketGuardWebSnapshot.ConnectedBreakLimit,
			MiningDuplicateTargetMs = antiCheatPacketGuardWebSnapshot.MiningDuplicateTargetMs,
			RejectConnectedBreakBursts = antiCheatPacketGuardWebSnapshot.RejectConnectedBreakBursts,
			MiningCadenceValidationEnabled = antiCheatPacketGuardWebSnapshot.MiningCadenceValidationEnabled,
			MiningCadenceMinExpectedMs = antiCheatPacketGuardWebSnapshot.MiningCadenceMinExpectedMs,
			MiningCadenceSuspiciousRatio = antiCheatPacketGuardWebSnapshot.MiningCadenceSuspiciousRatio,
			MiningCadenceConfirmations = antiCheatPacketGuardWebSnapshot.MiningCadenceConfirmations,
			MiningCadenceConfirmationWindowMs = antiCheatPacketGuardWebSnapshot.MiningCadenceConfirmationWindowMs,
			RejectImpossibleMiningCadence = antiCheatPacketGuardWebSnapshot.RejectImpossibleMiningCadence,
			TpsAwarenessEnabled = antiCheatPacketGuardWebSnapshot.TpsAwarenessEnabled,
			TpsDegradedPercent = antiCheatPacketGuardWebSnapshot.TpsDegradedPercent,
			TpsCriticalPercent = antiCheatPacketGuardWebSnapshot.TpsCriticalPercent,
			TpsMaxTimingMultiplier = antiCheatPacketGuardWebSnapshot.TpsMaxTimingMultiplier,
			TpsSuppressTimingRejectsWhenCritical = antiCheatPacketGuardWebSnapshot.TpsSuppressTimingRejectsWhenCritical,
			TpsReady = antiCheatPacketGuardWebSnapshot.TpsReady,
			TpsTarget = antiCheatPacketGuardWebSnapshot.TpsTarget,
			TpsEstimated = antiCheatPacketGuardWebSnapshot.TpsEstimated,
			TpsHealthPercent = antiCheatPacketGuardWebSnapshot.TpsHealthPercent,
			TpsBand = (antiCheatPacketGuardWebSnapshot.TpsBand ?? "Disabled"),
			PerFamilyRiskEnabled = antiCheatPacketGuardWebSnapshot.PerFamilyRiskEnabled,
			MovementRiskDecaySeconds = antiCheatPacketGuardWebSnapshot.MovementRiskDecaySeconds,
			MovementRiskReviewThreshold = antiCheatPacketGuardWebSnapshot.MovementRiskReviewThreshold,
			MovementRiskSuspiciousThreshold = antiCheatPacketGuardWebSnapshot.MovementRiskSuspiciousThreshold,
			ReachRiskDecaySeconds = antiCheatPacketGuardWebSnapshot.ReachRiskDecaySeconds,
			ReachRiskReviewThreshold = antiCheatPacketGuardWebSnapshot.ReachRiskReviewThreshold,
			ReachRiskSuspiciousThreshold = antiCheatPacketGuardWebSnapshot.ReachRiskSuspiciousThreshold,
			MiningRiskDecaySeconds = antiCheatPacketGuardWebSnapshot.MiningRiskDecaySeconds,
			MiningRiskReviewThreshold = antiCheatPacketGuardWebSnapshot.MiningRiskReviewThreshold,
			MiningRiskSuspiciousThreshold = antiCheatPacketGuardWebSnapshot.MiningRiskSuspiciousThreshold,
			PacketAbuseRiskDecaySeconds = antiCheatPacketGuardWebSnapshot.PacketAbuseRiskDecaySeconds,
			PacketAbuseRiskReviewThreshold = antiCheatPacketGuardWebSnapshot.PacketAbuseRiskReviewThreshold,
			PacketAbuseRiskSuspiciousThreshold = antiCheatPacketGuardWebSnapshot.PacketAbuseRiskSuspiciousThreshold,
			ClientIntegrityRiskDecaySeconds = antiCheatPacketGuardWebSnapshot.ClientIntegrityRiskDecaySeconds,
			ClientIntegrityRiskReviewThreshold = antiCheatPacketGuardWebSnapshot.ClientIntegrityRiskReviewThreshold,
			ClientIntegrityRiskSuspiciousThreshold = antiCheatPacketGuardWebSnapshot.ClientIntegrityRiskSuspiciousThreshold,
			IncidentCorrelationEnabled = antiCheatPacketGuardWebSnapshot.IncidentCorrelationEnabled,
			IncidentCorrelationWindowSeconds = antiCheatPacketGuardWebSnapshot.IncidentCorrelationWindowSeconds,
			IncidentCorrelationRequiredCategories = antiCheatPacketGuardWebSnapshot.IncidentCorrelationRequiredCategories,
			IncidentCorrelationMaxRiskBonus = antiCheatPacketGuardWebSnapshot.IncidentCorrelationMaxRiskBonus,
			HistoryEnabled = antiCheatHistorySummaryWebSnapshot.Enabled,
			HistoryRetentionDays = antiCheatHistorySummaryWebSnapshot.RetentionDays,
			HistoryMaxRecords = antiCheatHistorySummaryWebSnapshot.MaxRecords
		};
	}

	private static List<string> BuildAntiCheatPacketProtectionSummary(AntiCheatWebSnapshot snapshot)
	{
		AntiCheatPacketGuardWebSnapshot antiCheatPacketGuardWebSnapshot = snapshot.PacketGuard ?? new AntiCheatPacketGuardWebSnapshot();
		return new List<string>
		{
			$"PacketGuard: {(antiCheatPacketGuardWebSnapshot.Enabled ? "on" : "off")} • chat warnings {(antiCheatPacketGuardWebSnapshot.PlayerWarningEnabled ? "on" : "off")} • noclip {(antiCheatPacketGuardWebSnapshot.NoclipEnabled ? "on" : "off")}",
			$"Movement {(antiCheatPacketGuardWebSnapshot.MovementEnabled ? "on" : "off")} • reach {(antiCheatPacketGuardWebSnapshot.ReachEnabled ? "on" : "off")} • mining {(antiCheatPacketGuardWebSnapshot.MiningEnabled ? "on" : "off")} • TPS {(antiCheatPacketGuardWebSnapshot.TpsAwarenessEnabled ? "aware" : "off")}",
			$"Acceleration sample {antiCheatPacketGuardWebSnapshot.AccelerationSampleMinMs} ms • smoothing {antiCheatPacketGuardWebSnapshot.AccelerationSmoothingFactor:0.##} • mount ratio {antiCheatPacketGuardWebSnapshot.MountAccelerationMinSpeedRatio:0.##}",
			$"Per-family risk {(antiCheatPacketGuardWebSnapshot.PerFamilyRiskEnabled ? "on" : "off")} • diagnostics context {(antiCheatPacketGuardWebSnapshot.DiagnosticContextEnabled ? "on" : "off")} • suppressions {(antiCheatPacketGuardWebSnapshot.DiagnosticSuppressionEnabled ? "on" : "off")}"
		};
	}

	private static AntiCheatGuiSettingsPacket BuildAntiCheatSettingsPacket(AntiCheatWebSnapshot snapshot)
	{
		AntiCheatPacketGuardWebSnapshot antiCheatPacketGuardWebSnapshot = snapshot.PacketGuard ?? new AntiCheatPacketGuardWebSnapshot();
		AntiCheatHistorySummaryWebSnapshot antiCheatHistorySummaryWebSnapshot = snapshot.History ?? new AntiCheatHistorySummaryWebSnapshot();
		return new AntiCheatGuiSettingsPacket
		{
			RequireClient = snapshot.RequireClient,
			RescanMinutes = snapshot.RescanMinutes,
			RuntimeRescanOnChange = snapshot.RuntimeRescanOnChange,
			RuntimeChangeRescanCooldownSeconds = snapshot.RuntimeChangeRescanCooldownSeconds,
			SimilarityDetectionEnabled = snapshot.SimilarityDetectionEnabled,
			SimilarityReviewThresholdPercent = snapshot.SimilarityReviewThresholdPercent,
			SimilarityHighThresholdPercent = snapshot.SimilarityHighThresholdPercent,
			PacketGuardEnabled = antiCheatPacketGuardWebSnapshot.Enabled,
			MovementEnabled = antiCheatPacketGuardWebSnapshot.MovementEnabled,
			HardMoveDistance = antiCheatPacketGuardWebSnapshot.HardMoveDistance,
			RejectUnauthorizedMovement = antiCheatPacketGuardWebSnapshot.RejectUnauthorizedMovement,
			Th3EssentialsSpawnCompatibility = antiCheatPacketGuardWebSnapshot.Th3EssentialsSpawnCompatibility,
			Th3EssentialsSpawnGraceMs = antiCheatPacketGuardWebSnapshot.Th3EssentialsSpawnGraceMs,
			Th3EssentialsDetected = antiCheatPacketGuardWebSnapshot.Th3EssentialsDetected,
			ReachEnabled = antiCheatPacketGuardWebSnapshot.ReachEnabled,
			ReachTolerance = antiCheatPacketGuardWebSnapshot.ReachTolerance,
			ReachHardExcess = antiCheatPacketGuardWebSnapshot.ReachHardExcess,
			RejectOutOfReachActions = antiCheatPacketGuardWebSnapshot.RejectOutOfReachActions,
			MiningEnabled = antiCheatPacketGuardWebSnapshot.MiningEnabled,
			MiningCadenceValidationEnabled = antiCheatPacketGuardWebSnapshot.MiningCadenceValidationEnabled,
			MiningCadenceSuspiciousRatio = antiCheatPacketGuardWebSnapshot.MiningCadenceSuspiciousRatio,
			MiningCadenceConfirmations = antiCheatPacketGuardWebSnapshot.MiningCadenceConfirmations,
			RejectConnectedBreakBursts = antiCheatPacketGuardWebSnapshot.RejectConnectedBreakBursts,
			RejectImpossibleMiningCadence = antiCheatPacketGuardWebSnapshot.RejectImpossibleMiningCadence,
			TpsAwarenessEnabled = antiCheatPacketGuardWebSnapshot.TpsAwarenessEnabled,
			TpsDegradedPercent = antiCheatPacketGuardWebSnapshot.TpsDegradedPercent,
			TpsCriticalPercent = antiCheatPacketGuardWebSnapshot.TpsCriticalPercent,
			TpsMaxTimingMultiplier = antiCheatPacketGuardWebSnapshot.TpsMaxTimingMultiplier,
			TpsSuppressTimingRejectsWhenCritical = antiCheatPacketGuardWebSnapshot.TpsSuppressTimingRejectsWhenCritical,
			IncidentCorrelationEnabled = antiCheatPacketGuardWebSnapshot.IncidentCorrelationEnabled,
			IncidentCorrelationWindowSeconds = antiCheatPacketGuardWebSnapshot.IncidentCorrelationWindowSeconds,
			IncidentCorrelationRequiredCategories = antiCheatPacketGuardWebSnapshot.IncidentCorrelationRequiredCategories,
			IncidentCorrelationMaxRiskBonus = antiCheatPacketGuardWebSnapshot.IncidentCorrelationMaxRiskBonus,
			HistoryEnabled = antiCheatHistorySummaryWebSnapshot.Enabled,
			HistoryRetentionDays = antiCheatHistorySummaryWebSnapshot.RetentionDays,
			HistoryMaxRecords = antiCheatHistorySummaryWebSnapshot.MaxRecords
		};
	}

	private static List<string> BuildAntiCheatSettingsSummary(AntiCheatWebSnapshot snapshot)
	{
		return new List<string>
		{
			"Client requirement: " + (snapshot.RequireClient ? "required" : "optional"),
			$"Client rescan: every {snapshot.RescanMinutes} minute(s) • runtime-change rescan {(snapshot.RuntimeRescanOnChange ? "on" : "off")}",
			$"Similarity detection: {(snapshot.SimilarityDetectionEnabled ? "on" : "off")} • review {snapshot.SimilarityReviewThresholdPercent}% • high {snapshot.SimilarityHighThresholdPercent}%",
			"PacketGuard settings are managed under Anti-Cheat → Packet Protection."
		};
	}

	private static string ShortHash(string? value)
	{
		string text = (value ?? "").Trim();
		if (text.Length == 0)
		{
			return "n/a";
		}
		return (text.Length <= 16) ? text : (text.Substring(0, 16) + "...");
	}

	private static string FormatUtc(long unixMilliseconds)
	{
		if (unixMilliseconds <= 0)
		{
			return "never";
		}
		try
		{
			return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
		}
		catch
		{
			return unixMilliseconds.ToString(CultureInfo.InvariantCulture);
		}
	}

	private void SendAntiCheatGuiResponse(IServerPlayer player, long requestId, bool success, string operation, string message, List<string> lines, string? selectedPlayer, AntiCheatGuiSettingsPacket? settings = null, List<AntiCheatConsequenceRuleGuiPacket>? consequenceRules = null, AntiCheatPacketProtectionGuiPacket? packetSettings = null)
	{
		channel?.SendPacket(new AntiCheatGuiResponse
		{
			RequestId = requestId,
			Success = success,
			Operation = (operation ?? "overview"),
			Message = (message ?? ""),
			Lines = (lines ?? new List<string>()),
			SelectedPlayer = (selectedPlayer ?? ""),
			Enabled = (module.AntiCheat?.Config.Enabled ?? false),
			Settings = settings,
			ConsequenceRules = (consequenceRules ?? new List<AntiCheatConsequenceRuleGuiPacket>()),
			PacketSettings = packetSettings
		}, player);
	}

	private void HandleWebDashboard(IServerPlayer player, WebDashboardRequest request)
	{
		if (channel == null)
		{
			return;
		}
		string text = (request.Operation ?? "status").Trim().ToLowerInvariant();
		if (text == "panel")
		{
			text = "status";
		}
		bool flag;
		switch (text)
		{
		case "setbool":
		case "setint":
		case "settext":
		case "mapsave":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			if (!player.HasPrivilege("controlserver"))
			{
				SendWebDashboardResponse(player, request.RequestId, success: false, "You need the controlserver privilege to change Web Server settings.");
				return;
			}
		}
		else if (!player.HasPrivilege("blocklogger"))
		{
			SendWebDashboardResponse(player, request.RequestId, success: false, "You do not have the blocklogger privilege.");
			return;
		}
		if ((text == "map" || text == "mapsave") ? true : false)
		{
			HandleWebMapGuiRequest(player, request, text);
			return;
		}
		WebDashboardService webDashboard = module.WebDashboard;
		if (webDashboard == null)
		{
			SendWebDashboardResponse(player, request.RequestId, success: false, "Web Dashboard service is not available.");
			return;
		}
		try
		{
			string message = "Web Server status";
			bool success = true;
			string text2 = (request.SettingKey ?? "").Trim().ToLowerInvariant();
			switch (text)
			{
			case "setbool":
			{
				if (1 == 0)
				{
				}
				string text3;
				if (text2 == "enabled")
				{
					text3 = webDashboard.SetEnabled(request.BoolValue);
				}
				else
				{
					text3 = ((!(text2 == "securecookies")) ? ("Unknown Web Server setting '" + request.SettingKey + "'.") : webDashboard.SetSecureCookies(request.BoolValue));
				}
				if (1 == 0)
				{
				}
				message = text3;
				flag = ((text2 == "enabled" || text2 == "securecookies") ? true : false);
				success = flag;
				break;
			}
			case "setint":
			{
				if (1 == 0)
				{
				}
				string text3;
				if (text2 == "port")
				{
					text3 = webDashboard.SetPort(request.IntValue);
				}
				else
				{
					text3 = ((!(text2 == "sessiontimeoutminutes")) ? ("Unknown Web Server setting '" + request.SettingKey + "'.") : webDashboard.SetSessionTimeout(request.IntValue));
				}
				if (1 == 0)
				{
				}
				message = text3;
				flag = ((text2 == "port" || text2 == "sessiontimeoutminutes") ? true : false);
				success = flag;
				break;
			}
			case "settext":
			{
				if (1 == 0)
				{
				}
				string text3 = text2 switch
				{
					"bindaddress" => webDashboard.SetBindAddress(request.StringValue), 
					"publicurl" => webDashboard.SetPublicUrl(request.StringValue), 
					"adminusername" => webDashboard.SetUsername(request.StringValue), 
					_ => "Unknown Web Server setting '" + request.SettingKey + "'.", 
				};
				if (1 == 0)
				{
				}
				message = text3;
				switch (text2)
				{
				case "bindaddress":
				case "publicurl":
				case "adminusername":
					flag = true;
					break;
				default:
					flag = false;
					break;
				}
				success = flag;
				break;
			}
			case "status":
			case "settings":
				flag = true;
				goto IL_03c4;
			default:
				{
					flag = false;
					goto IL_03c4;
				}
				IL_03c4:
				if (!flag)
				{
					SendWebDashboardResponse(player, request.RequestId, success: false, "Unknown Web Server operation '" + request.Operation + "'.");
					return;
				}
				break;
			}
			SendWebDashboardResponse(player, request.RequestId, success, message);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[WebDashboard] GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			SendWebDashboardResponse(player, request.RequestId, success: false, "Web Server request failed. Check the server log.");
		}
	}

	private void HandleWebMapGuiRequest(IServerPlayer player, WebDashboardRequest request, string op)
	{
		WebMapBridgeService webMap = module.WebMap;
		if (webMap == null)
		{
			SendWebDashboardResponse(player, request.RequestId, success: false, "Web Map feature is not available in this running server configuration.");
			return;
		}
		try
		{
			if (op == "mapsave")
			{
				WebMapGuiSettingsPacket webMapSettings = request.WebMapSettings;
				if (webMapSettings == null)
				{
					SendWebDashboardResponse(player, request.RequestId, success: false, "No Web Map settings were supplied.");
					return;
				}
				WebMapConfig config = webMap.Config;
				config.Enabled = webMapSettings.Enabled;
				config.BindAddress = webMapSettings.BindAddress ?? "0.0.0.0";
				config.Port = webMapSettings.Port;
				config.Title = webMapSettings.Title ?? "Newfie's Server Map";
				config.PublicUrl = webMapSettings.PublicUrl ?? "";
				config.DashboardUrl = webMapSettings.DashboardUrl ?? "";
				config.AllowLivePlayerData = webMapSettings.ShowPlayers;
				config.ShowPlayers = webMapSettings.ShowPlayers;
				config.ShowWaypoints = webMapSettings.ShowWaypoints;
				config.ShowActivity = webMapSettings.ShowActivity;
				config.ShowBadBlockEntities = webMapSettings.ShowBadBlockEntities;
				config.PlayerMajorWaypointLimit = webMapSettings.PlayerMajorWaypointLimit;
				config.PlayerMinorWaypointLimit = webMapSettings.PlayerMinorWaypointLimit;
				config.MaximumZoomOutSpanBlocks = webMapSettings.MaximumZoomOutSpanBlocks;
				config.DefaultViewSpanBlocks = webMapSettings.DefaultViewSpanBlocks;
				config.TerrainTileSize = webMapSettings.TerrainTileSize;
				config.ActivityWindowMinutes = webMapSettings.ActivityWindowMinutes;
				config.MaxActivityMarkers = webMapSettings.MaxActivityMarkers;
				config.ExportTerrainOnStart = webMapSettings.ExportTerrainOnStart;
				config.AutomaticStartupExportEnabled = webMapSettings.AutomaticStartupExportEnabled;
				config.AutomaticStartupExportIntervalDays = webMapSettings.AutomaticStartupExportIntervalDays;
				config.UseIncrementalTerrainExport = webMapSettings.UseIncrementalTerrainExport;
				config.LockServerDuringTerrainExport = webMapSettings.LockServerDuringTerrainExport;
				webMap.SaveConfig(config);
				SendWebDashboardResponse(player, request.RequestId, success: true, "Web Map settings saved.");
			}
			else
			{
				SendWebDashboardResponse(player, request.RequestId, success: true, "Web Map configuration");
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[WebMap] Shift+L GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			SendWebDashboardResponse(player, request.RequestId, success: false, "Web Map request failed. Check the server log.");
		}
	}

	private WebMapGuiSettingsPacket? BuildWebMapGuiSettings()
	{
		WebMapBridgeService webMap = module.WebMap;
		if (webMap == null)
		{
			return null;
		}
		WebMapConfig config = webMap.Config;
		return new WebMapGuiSettingsPacket
		{
			Enabled = config.Enabled,
			Running = webMap.IsRunning,
			BindAddress = config.BindAddress,
			Port = config.Port,
			Title = config.Title,
			PublicUrl = config.PublicUrl,
			DashboardUrl = config.DashboardUrl,
			ShowPlayers = (config.AllowLivePlayerData && config.ShowPlayers),
			ShowWaypoints = config.ShowWaypoints,
			ShowActivity = config.ShowActivity,
			ShowBadBlockEntities = config.ShowBadBlockEntities,
			PlayerMajorWaypointLimit = config.PlayerMajorWaypointLimit,
			PlayerMinorWaypointLimit = config.PlayerMinorWaypointLimit,
			MaximumZoomOutSpanBlocks = config.MaximumZoomOutSpanBlocks,
			DefaultViewSpanBlocks = config.DefaultViewSpanBlocks,
			TerrainTileSize = config.TerrainTileSize,
			ActivityWindowMinutes = config.ActivityWindowMinutes,
			MaxActivityMarkers = config.MaxActivityMarkers,
			ExportTerrainOnStart = config.ExportTerrainOnStart,
			AutomaticStartupExportEnabled = config.AutomaticStartupExportEnabled,
			AutomaticStartupExportIntervalDays = config.AutomaticStartupExportIntervalDays,
			UseIncrementalTerrainExport = config.UseIncrementalTerrainExport,
			LockServerDuringTerrainExport = config.LockServerDuringTerrainExport,
			TerrainExportInProgress = webMap.IsTerrainExportInProgress,
			LastError = (webMap.LastError ?? "")
		};
	}

	private void SendWebDashboardResponse(IServerPlayer player, long requestId, bool success, string message)
	{
		if (channel != null)
		{
			WebDashboardService webDashboard = module.WebDashboard;
			WebDashboardConfig webDashboardConfig = webDashboard?.Config;
			channel.SendPacket(new WebDashboardResponse
			{
				RequestId = requestId,
				Success = success,
				Message = message,
				Lines = (webDashboard?.GetGuiStatusLines() ?? new List<string>()),
				Enabled = (webDashboardConfig?.Enabled ?? false),
				Running = (webDashboard?.IsRunning ?? false),
				BindAddress = (webDashboardConfig?.BindAddress ?? ""),
				Port = (webDashboardConfig?.Port ?? 0),
				AdminUsername = (webDashboardConfig?.AdminUsername ?? ""),
				PasswordConfigured = (webDashboard?.PasswordConfigured ?? false),
				ActiveSessions = (webDashboard?.ActiveSessionCount ?? 0),
				SessionTimeoutMinutes = (webDashboardConfig?.SessionTimeoutMinutes ?? 0),
				SecureCookies = (webDashboardConfig?.SecureCookies ?? false),
				LastError = (webDashboard?.LastError ?? ""),
				LocalAnonymousMode = (webDashboard?.LocalAnonymousMode ?? false),
				LocalUrl = (webDashboard?.LocalDashboardUrl ?? ""),
				PublicUrl = (webDashboard?.PublicDashboardUrl ?? ""),
				WebMapSettings = BuildWebMapGuiSettings()
			}, player);
		}
	}

	private void HandleLoginProtection(IServerPlayer player, LoginProtectionRequest request)
	{
		if (channel == null)
		{
			return;
		}
		string text = (request.Operation ?? "status").Trim().ToLowerInvariant();
		if ((text == "setbool" || text == "setint") ? true : false)
		{
			if (!player.HasPrivilege("controlserver"))
			{
				SendLoginProtectionResponse(player, request.RequestId, success: false, "You need the controlserver privilege to change Login Protection settings.", text);
				return;
			}
		}
		else if (!player.HasPrivilege("blocklogger"))
		{
			SendLoginProtectionResponse(player, request.RequestId, success: false, "You do not have the blocklogger privilege.", text);
			return;
		}
		LoginProtectionSystem current = LoginProtectionSystem.Current;
		if (current == null)
		{
			SendLoginProtectionResponse(player, request.RequestId, success: false, "Login Protection is not available.", text);
			return;
		}
		try
		{
			string message;
			switch (text)
			{
			case "setbool":
				message = current.SetBooleanSettingFromGui(request.SettingKey, request.BoolValue);
				break;
			case "setint":
				message = current.SetIntegerSettingFromGui(request.SettingKey, request.IntValue);
				break;
			case "settings":
				message = "Login Protection settings";
				break;
			case "status":
				message = "Login Protection status";
				break;
			default:
				SendLoginProtectionResponse(player, request.RequestId, success: false, "Unknown Login Protection operation '" + request.Operation + "'.", text);
				return;
			}
			SendLoginProtectionResponse(player, request.RequestId, success: true, message, text);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[LoginProtection] GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			SendLoginProtectionResponse(player, request.RequestId, success: false, "Login Protection request failed. Check the server log.", text);
		}
	}

	private void SendLoginProtectionResponse(IServerPlayer player, long requestId, bool success, string message, string operation)
	{
		if (channel == null)
		{
			return;
		}
		LoginProtectionSystem current = LoginProtectionSystem.Current;
		if (current == null)
		{
			channel.SendPacket(new LoginProtectionResponse
			{
				RequestId = requestId,
				Success = false,
				Message = "Login Protection is not available."
			}, player);
			return;
		}
		LoginProtectionConfig config = current.Config;
		bool flag;
		switch (operation)
		{
		case "settings":
		case "setbool":
		case "setint":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool flag2 = flag;
		channel.SendPacket(new LoginProtectionResponse
		{
			RequestId = requestId,
			Success = success,
			Message = message,
			Lines = (flag2 ? current.GetGuiSettingsLines() : current.GetGuiStatusLines()),
			Enabled = config.Enabled,
			ProtectionSeconds = config.ProtectionSeconds,
			ProtectOnJoin = config.ProtectOnJoin,
			ProtectOnRespawn = config.ProtectOnRespawn,
			AnnounceOnJoin = config.AnnounceOnJoin,
			AnnounceOnEnd = config.AnnounceOnEnd,
			BlockPvP = config.BlockPvP,
			CancelOnAttack = config.CancelOnAttack,
			ProtectFallDamage = config.ProtectFallDamage,
			ExtinguishFire = config.ExtinguishFire,
			ActiveProtectedCount = current.ActiveProtectedCount,
			RecoverSwimmingOnLogin = config.RecoverSwimmingOnLogin
		}, player);
	}

	private void HandleProfiler(IServerPlayer player, ProfilerRequest request)
	{
		if (channel == null)
		{
			return;
		}
		string text = (request.Operation ?? "get").Trim().ToLowerInvariant();
		if ((text == "start" || text == "setenabled") ? true : false)
		{
			if (!player.HasPrivilege("controlserver"))
			{
				SendProfilerResponse(player, request.RequestId, success: false, "You need the controlserver privilege to start an engine profile.");
				return;
			}
		}
		else if (!player.HasPrivilege("blocklogger"))
		{
			SendProfilerResponse(player, request.RequestId, success: false, "You do not have the blocklogger privilege.");
			return;
		}
		ServerHealthService serverHealth = module.ServerHealth;
		if (serverHealth == null)
		{
			SendProfilerResponse(player, request.RequestId, success: false, "Server diagnostics are not available.");
			return;
		}
		if (text == "get" && request.Compact)
		{
			SendProfilerCompactResponse(player, request.RequestId);
			return;
		}
		try
		{
			string message = "Server profiler & diagnostics";
			bool success = true;
			if (text == "setenabled")
			{
				module.Config.ProfilerEnabled = request.BoolValue;
				module.SaveRuntimeConfig();
				message = (request.BoolValue ? "Profiler enabled. Rolling diagnostics are now collecting." : "Profiler disabled. Rolling diagnostics and any active engine profile were stopped.");
			}
			else if (text == "start")
			{
				success = serverHealth.StartManualProfile(request.DurationSeconds, out message);
			}
			else if (text != "get")
			{
				SendProfilerResponse(player, request.RequestId, success: false, "Unknown profiler operation '" + request.Operation + "'.");
				return;
			}
			SendProfilerResponse(player, request.RequestId, success, message);
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerHealth] GUI profiler request failed: {0}: {1}", ex.GetType().Name, ex.Message);
			SendProfilerResponse(player, request.RequestId, success: false, "Profiler request failed. Check the server log.");
		}
	}

	private void SendProfilerCompactResponse(IServerPlayer player, long requestId)
	{
		if (channel != null)
		{
			ServerHealthService serverHealth = module.ServerHealth;
			if (serverHealth == null)
			{
				channel.SendPacket(new ProfilerResponse
				{
					RequestId = requestId,
					Success = false,
					Compact = true,
					Message = "Server diagnostics are not available."
				}, player);
			}
			else
			{
				ServerHealthLiveState liveState = serverHealth.GetLiveState();
				channel.SendPacket(new ProfilerResponse
				{
					RequestId = requestId,
					Success = true,
					Compact = true,
					Enabled = liveState.Enabled,
					ProfileRunning = liveState.ProfileRunning,
					ProfileDurationSeconds = liveState.ProfileDurationSeconds,
					ProfileRemainingSeconds = liveState.ProfileRemainingSeconds,
					Current = ToProfilerSample(liveState.Current)
				}, player);
			}
		}
	}

	private void SendProfilerResponse(IServerPlayer player, long requestId, bool success, string message)
	{
		if (channel == null)
		{
			return;
		}
		ServerHealthService serverHealth = module.ServerHealth;
		if (serverHealth == null)
		{
			channel.SendPacket(new ProfilerResponse
			{
				RequestId = requestId,
				Success = false,
				Message = "Server diagnostics are not available."
			}, player);
			return;
		}
		ServerHealthSnapshot snapshot = serverHealth.GetSnapshot();
		channel.SendPacket(new ProfilerResponse
		{
			RequestId = requestId,
			Success = success,
			Message = message,
			Enabled = snapshot.Enabled,
			ProfileRunning = snapshot.ProfileRunning,
			ProfileDurationSeconds = snapshot.ProfileDurationSeconds,
			ProfileRemainingSeconds = snapshot.ProfileRemainingSeconds,
			Current = ToProfilerSample(snapshot.Current),
			LastProfile = new ProfilerProfileSummaryPacket
			{
				CompletedUnixMilliseconds = snapshot.LastProfile.CompletedUnixMilliseconds,
				DurationSeconds = snapshot.LastProfile.DurationSeconds,
				TickCount = snapshot.LastProfile.TickCount,
				AverageTickMs = snapshot.LastProfile.AverageTickMs,
				P95TickMs = snapshot.LastProfile.P95TickMs,
				P99TickMs = snapshot.LastProfile.P99TickMs,
				MaxTickMs = snapshot.LastProfile.MaxTickMs,
				AttributedSegments = snapshot.LastProfile.AttributedSegments,
				UnattributedSegments = snapshot.LastProfile.UnattributedSegments,
				AttributedMs = snapshot.LastProfile.AttributedMs,
				UnattributedMs = snapshot.LastProfile.UnattributedMs
			},
			Samples = snapshot.Samples.Select(ToProfilerSample).ToList(),
			SlowSpikes = snapshot.SlowSpikes.Select((ServerSlowSpike x) => new ProfilerSlowSpikePacket
			{
				UnixMilliseconds = x.UnixMilliseconds,
				DelayMs = x.DelayMs,
				CpuPercent = x.CpuPercent,
				WorkingSetMb = x.WorkingSetMb,
				Players = x.Players,
				LoadedEntities = x.LoadedEntities,
				LoadedChunks = x.LoadedChunks,
				BlockLoggerQueue = x.BlockLoggerQueue,
				BlockLoggerWriterMs = x.BlockLoggerWriterMs,
				Gen0Delta = x.Gen0Delta,
				Gen1Delta = x.Gen1Delta,
				Gen2Delta = x.Gen2Delta
			}).ToList(),
			Hotspots = snapshot.Hotspots.Select((ServerProfilerHotspot x) => new ProfilerHotspotPacket
			{
				Code = x.Code,
				TotalMs = x.TotalMs,
				MaxSingleMs = x.MaxSingleMs,
				Calls = x.Calls,
				PercentOfProfileTickTime = x.PercentOfProfileTickTime,
				ModId = x.ModId,
				ModName = x.ModName,
				AttributionConfidence = x.AttributionConfidence,
				IsCore = x.IsCore
			}).ToList(),
			ModHotspots = snapshot.ModHotspots.Select((ServerProfilerModHotspot x) => new ProfilerModHotspotPacket
			{
				ModId = x.ModId,
				ModName = x.ModName,
				TotalMs = x.TotalMs,
				MaxSingleMs = x.MaxSingleMs,
				Calls = x.Calls,
				SlowSegments = x.SlowSegments,
				PercentOfProfileTickTime = x.PercentOfProfileTickTime,
				Confidence = x.Confidence,
				TopMarker = x.TopMarker,
				IsCore = x.IsCore
			}).ToList(),
			Phases = snapshot.Phases.Select((ServerProfilerPhase x) => new ProfilerPhasePacket
			{
				Code = x.Code,
				TotalMs = x.TotalMs,
				AveragePerTickMs = x.AveragePerTickMs,
				MaxSingleMs = x.MaxSingleMs,
				Calls = x.Calls,
				PercentOfProfileTickTime = x.PercentOfProfileTickTime,
				ModId = x.ModId,
				AttributionConfidence = x.AttributionConfidence
			}).ToList(),
			ProfileSlowTicks = snapshot.ProfileSlowTicks.Select((ServerProfileSlowTick x) => new ProfilerProfileSlowTickPacket
			{
				UnixMilliseconds = x.UnixMilliseconds,
				TickMs = x.TickMs,
				TopCauseSummary = x.TopCauseSummary
			}).ToList(),
			OffThreadReports = snapshot.OffThreadReports.Select((ServerProfilerOffThreadReport x) => new ProfilerOffThreadReportPacket
			{
				DurationMs = x.DurationMs,
				Summary = x.Summary,
				ModId = x.ModId,
				Confidence = x.Confidence
			}).ToList(),
			DiagnosticIncidents = snapshot.DiagnosticIncidents.Select((ServerDiagnosticIncident x) => new ProfilerDiagnosticIncidentPacket
			{
				UnixMilliseconds = x.UnixMilliseconds,
				Severity = x.Severity,
				DelayMs = x.DelayMs,
				CpuPercent = x.CpuPercent,
				ManagedHeapMb = x.ManagedHeapMb,
				LoadedEntities = x.LoadedEntities,
				EntityDelta = x.EntityDelta,
				LoadedChunks = x.LoadedChunks,
				ChunkDelta = x.ChunkDelta,
				BlockLoggerQueue = x.BlockLoggerQueue,
				BlockLoggerWriterMs = x.BlockLoggerWriterMs,
				BlockLoggerDroppedDelta = x.BlockLoggerDroppedDelta,
				Gen0Delta = x.Gen0Delta,
				Gen1Delta = x.Gen1Delta,
				Gen2Delta = x.Gen2Delta,
				Evidence = x.Evidence
			}).ToList(),
			DiagnosticSummary = new ProfilerDiagnosticSummaryPacket
			{
				State = snapshot.DiagnosticSummary.State,
				WindowMinutes = snapshot.DiagnosticSummary.WindowMinutes,
				Summary = snapshot.DiagnosticSummary.Summary,
				StallCount = snapshot.DiagnosticSummary.StallCount,
				SevereStallCount = snapshot.DiagnosticSummary.SevereStallCount,
				WorstDelayMs = snapshot.DiagnosticSummary.WorstDelayMs,
				AverageCpuPercent = snapshot.DiagnosticSummary.AverageCpuPercent,
				MaxCpuPercent = snapshot.DiagnosticSummary.MaxCpuPercent,
				Gen2Collections = snapshot.DiagnosticSummary.Gen2Collections,
				MaxBlockLoggerQueue = snapshot.DiagnosticSummary.MaxBlockLoggerQueue,
				MaxBlockLoggerWriterMs = snapshot.DiagnosticSummary.MaxBlockLoggerWriterMs,
				BlockLoggerDroppedIncrease = snapshot.DiagnosticSummary.BlockLoggerDroppedIncrease,
				MaxLoadedEntities = snapshot.DiagnosticSummary.MaxLoadedEntities,
				MaxLoadedChunks = snapshot.DiagnosticSummary.MaxLoadedChunks
			},
			LoadedModCount = snapshot.LoadedModCount,
			LocalWebUrl = (module.WebDashboard?.LocalDashboardUrl ?? ""),
			PublicWebUrl = (module.WebDashboard?.PublicDashboardUrl ?? "")
		}, player);
	}

	private static ProfilerSamplePacket ToProfilerSample(ServerHealthSample sample)
	{
		return new ProfilerSamplePacket
		{
			UnixMilliseconds = sample.UnixMilliseconds,
			MainThreadDelayMs = sample.MainThreadDelayMs,
			CpuPercent = sample.CpuPercent,
			WorkingSetMb = sample.WorkingSetMb,
			ManagedHeapMb = sample.ManagedHeapMb,
			Gen0Collections = sample.Gen0Collections,
			Gen1Collections = sample.Gen1Collections,
			Gen2Collections = sample.Gen2Collections,
			Players = sample.Players,
			LoadedEntities = sample.LoadedEntities,
			LoadedChunks = sample.LoadedChunks,
			BlockLoggerQueue = sample.BlockLoggerQueue,
			BlockLoggerWriterMs = sample.BlockLoggerWriterMs,
			BlockLoggerDropped = sample.BlockLoggerDropped,
			AnimalTrackedEntities = sample.AnimalTrackedEntities,
			AnimalTrackedRegions = sample.AnimalTrackedRegions
		};
	}

	private void HandleStatus(IServerPlayer player, InvestigatorStatusRequest request)
	{
		if (channel == null)
		{
			return;
		}
		if (!player.HasPrivilege("blocklogger"))
		{
			channel.SendPacket(new InvestigatorStatusResponse
			{
				RequestId = request.RequestId,
				Error = "You do not have the blocklogger privilege."
			}, player);
			return;
		}
		LogStorageService storage = module.Storage;
		if (storage == null)
		{
			channel.SendPacket(new InvestigatorStatusResponse
			{
				RequestId = request.RequestId,
				Error = "BlockLogger storage is not available."
			}, player);
			return;
		}
		BlockLoggerConfig config = module.Config;
		List<string> lines = new List<string>
		{
			$"Enabled: {config.Enabled}",
			$"Database ready: {storage.IsReady}",
			$"Pending audit events: {storage.PendingEvents:N0}",
			$"Peak pending: {storage.PeakPendingEvents:N0}",
			$"Dropped audit events: {storage.DroppedEvents:N0}",
			$"Written audit events: {storage.WrittenEvents:N0}",
			$"Written entity events: {storage.WrittenEntityEvents:N0}",
			$"Entity aggregates: {storage.WrittenEntityAggregates:N0} written, {storage.PendingEntityAggregates:N0} pending, {storage.DroppedEntityAggregates:N0} dropped",
			$"Last writer batch: {storage.LastBatchRows:N0} rows in {storage.LastBatchElapsedMs:N0} ms",
			$"Snapshots pending: {storage.PendingSnapshots:N0}",
			$"Retention: {config.RetentionDays} days",
			$"Max query results: {config.MaxDisplayCount:N0} (NewfiesBlocklogger.json -> MaxDisplayCount)",
			$"Entity aggregation: {(config.AggregateEntitySpawnDespawn ? "on" : "off")}, {config.EntityAggregateWindowMs}-{config.EntityAggregateMaxWindowMs} ms",
			$"Queue limit: {config.MaxQueuedEvents:N0}; flush interval: {config.FlushIntervalMs} ms"
		};
		channel.SendPacket(new InvestigatorStatusResponse
		{
			RequestId = request.RequestId,
			Success = true,
			Lines = lines
		}, player);
	}

	private void HandleQuery(IServerPlayer player, InvestigatorQueryRequest request)
	{
		if (channel == null)
		{
			return;
		}
		if (!player.HasPrivilege("blocklogger"))
		{
			SendError(player, request, "You do not have the blocklogger privilege.");
			return;
		}
		LogQueryService queries = module.Queries;
		if (queries == null)
		{
			SendError(player, request, "BlockLogger query service is not available.");
			return;
		}
		if (!activeQueries.TryAdd(player.PlayerUID, 0))
		{
			SendError(player, request, "A BlockLogger search is already running.");
			return;
		}
		byte value;
		try
		{
			string text = NormalizeView(request.View);
			BlockLoggerConfig config = module.Config;
			int radius = Math.Clamp(request.Radius, 0, config.MaxRadius);
			int limit = Math.Clamp(request.Limit, 1, config.MaxDisplayCount);
			string text2 = TrimFilter(request.Search, 80);
			string text3 = NormalizeAction(text, request.Action);
			BlockPos pos;
			if (text == "players")
			{
				if (string.IsNullOrWhiteSpace(text2))
				{
					activeQueries.TryRemove(player.PlayerUID, out value);
					SendError(player, request, "Enter a player name or UID.");
				}
				else
				{
					QueryPlayerAsync(player, request.RequestId, text2, limit, text3, queries);
				}
			}
			else if (!TryResolveTarget(player, request.UseSelection, text, out pos))
			{
				activeQueries.TryRemove(player.PlayerUID, out value);
				SendError(player, request, request.UseSelection ? "Look at a block or entity first, or use Current position." : "Unable to resolve your current position.");
			}
			else
			{
				QueryAsync(player, request.RequestId, text, pos, radius, limit, text2, text3, queries);
			}
		}
		catch (Exception ex)
		{
			activeQueries.TryRemove(player.PlayerUID, out value);
			api.Logger.Warning("[BlockLogger] Investigator request rejected: {0}: {1}", ex.GetType().Name, ex.Message);
			SendError(player, request, "Invalid search request.");
		}
	}

	private async Task QueryPlayerAsync(IServerPlayer player, long requestId, string playerFilter, int limit, string? category, LogQueryService queries)
	{
		try
		{
			string languageCode = player.LanguageCode;
			List<LogEventRecord> rows = await queries.QueryPlayerAsync(playerFilter, limit, category).ConfigureAwait(continueOnCapturedContext: false);
			List<string> lines = new List<string>(rows.Count);
			foreach (LogEventRecord row in rows)
			{
				lines.Add(FormatPlayerLine(row, languageCode));
			}
			InvestigatorQueryResponse response = new InvestigatorQueryResponse
			{
				RequestId = requestId,
				Success = true,
				View = "players",
				Header = $"Player history — {playerFilter} — {rows.Count} result(s)",
				Lines = lines
			};
			api.Event.EnqueueMainThreadTask(() =>
			{
				channel?.SendPacket(response, player);
			}, "blocklogger-player-result");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			api.Logger.Warning("[BlockLogger] Player history query failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
			InvestigatorQueryResponse response2 = new InvestigatorQueryResponse
			{
				RequestId = requestId,
				View = "players",
				Error = "Player history search failed. Check the server log."
			};
			api.Event.EnqueueMainThreadTask(() =>
			{
				channel?.SendPacket(response2, player);
			}, "blocklogger-player-error");
		}
		finally
		{
			activeQueries.TryRemove(player.PlayerUID, out var _);
		}
	}

	private async Task QueryAsync(IServerPlayer player, long requestId, string view, BlockPos target, int radius, int limit, string? search, string? action, LogQueryService queries)
	{
		try
		{
			string languageCode = player.LanguageCode;
			List<LogEventRecord> list = ((!(view == "entities")) ? (await queries.QueryBlocksAsync(target.X, target.Y, target.Z, target.dimension, radius, limit, search, action).ConfigureAwait(continueOnCapturedContext: false)) : (await queries.QueryEntitiesAsync(target.X, target.Y, target.Z, target.dimension, radius, limit, search, action).ConfigureAwait(continueOnCapturedContext: false)));
			List<LogEventRecord> rows = list;
			List<string> lines = new List<string>(rows.Count);
			foreach (LogEventRecord row in rows)
			{
				lines.Add(FormatLine(row, target, languageCode));
			}
			InvestigatorQueryResponse response = new InvestigatorQueryResponse
			{
				RequestId = requestId,
				Success = true,
				View = view,
				Header = $"{Title(view)} — {rows.Count} result(s), radius {radius}, target {target.X}, {target.Y}, {target.Z}",
				Lines = lines
			};
			api.Event.EnqueueMainThreadTask(() =>
			{
				channel?.SendPacket(response, player);
			}, "blocklogger-investigator-result");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			api.Logger.Warning("[BlockLogger] Investigator query failed: {0}: {1}", ex2.GetType().Name, ex2.Message);
			InvestigatorQueryResponse response2 = new InvestigatorQueryResponse
			{
				RequestId = requestId,
				View = view,
				Error = "Search failed. Check the server log."
			};
			api.Event.EnqueueMainThreadTask(() =>
			{
				channel?.SendPacket(response2, player);
			}, "blocklogger-investigator-error");
		}
		finally
		{
			activeQueries.TryRemove(player.PlayerUID, out var _);
		}
	}

	private void SendError(IServerPlayer player, InvestigatorQueryRequest request, string error)
	{
		channel?.SendPacket(new InvestigatorQueryResponse
		{
			RequestId = request.RequestId,
			View = NormalizeView(request.View),
			Error = error
		}, player);
	}

	private static bool TryResolveTarget(IServerPlayer player, bool useSelection, string view, out BlockPos pos)
	{
		pos = new BlockPos(0);
		if (!useSelection)
		{
			return EntityPositionResolver.TryGetBlockPos(player.Entity, out pos);
		}
		if (view == "entities" && player.CurrentEntitySelection?.Entity != null && EntityPositionResolver.TryGetBlockPos(player.CurrentEntitySelection.Entity, out pos))
		{
			return true;
		}
		if (player.CurrentBlockSelection?.Position != null)
		{
			pos = player.CurrentBlockSelection.Position.Copy();
			return true;
		}
		return player.CurrentEntitySelection?.Entity != null && EntityPositionResolver.TryGetBlockPos(player.CurrentEntitySelection.Entity, out pos);
	}

	private static string NormalizeView(string? view)
	{
		string text = view?.Trim().ToLowerInvariant();
		if (1 == 0)
		{
		}
		string result = text switch
		{
			"containers" => "containers", 
			"entities" => "entities", 
			"players" => "players", 
			_ => "blocks", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string? NormalizeAction(string view, string? action)
	{
		string text = action?.Trim().ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(text) || text == "any")
		{
			return null;
		}
		if (1 == 0)
		{
		}
		string result;
		switch (view)
		{
		case "containers":
		{
			bool flag;
			switch (text)
			{
			case "open":
			case "take":
			case "put":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			result = (flag ? text : "container");
			break;
		}
		case "entities":
		{
			bool flag;
			switch (text)
			{
			case "entityspawn":
			case "entitydeath":
			case "entitydespawn":
			case "entityinteract":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			result = (flag ? text : null);
			break;
		}
		case "players":
		{
			bool flag;
			switch (text)
			{
			case "blocks":
			case "containers":
			case "entities":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			result = (flag ? text : null);
			break;
		}
		default:
		{
			bool flag;
			switch (text)
			{
			case "break":
			case "place":
			case "interact":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			result = (flag ? text : null);
			break;
		}
		}
		if (1 == 0)
		{
		}
		return result;
	}

	private static string? TrimFilter(string? value, int maxLength)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		value = value.Trim();
		return (value.Length <= maxLength) ? value : value.Substring(0, maxLength);
	}

	private static string Title(string view)
	{
		if (1 == 0)
		{
		}
		string result = view switch
		{
			"containers" => "Container history", 
			"entities" => "Entity history", 
			"players" => "Player history", 
			_ => "Block history", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string FormatPlayerLine(LogEventRecord record, string? languageCode)
	{
		string value = (string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : record.PlayerName);
		string text = ((!string.IsNullOrWhiteSpace(record.BlockCode)) ? record.BlockCode : (Meta(record, "entityCode") ?? "unknown"));
		LogActionType action = record.Action;
		if (1 == 0)
		{
		}
		string text2 = action switch
		{
			LogActionType.ContainerTake => $"took {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} from {text}", 
			LogActionType.ContainerPut => $"put {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} into {text}", 
			LogActionType.ContainerOpen => "opened " + text, 
			LogActionType.BlockBreak => "broke " + text, 
			LogActionType.BlockPlace => "placed " + text, 
			LogActionType.BlockUse => "used " + text, 
			LogActionType.EntityInteract => "interacted with " + (Meta(record, "entityCode") ?? "entity"), 
			LogActionType.EntitySpawn => "spawned " + (Meta(record, "entityCode") ?? "entity") + CountSuffix(record), 
			LogActionType.EntityDeath => (Meta(record, "entityCode") ?? "entity") + " died", 
			LogActionType.EntityDespawn => "despawned " + (Meta(record, "entityCode") ?? "entity") + CountSuffix(record), 
			_ => record.Action.ToString(), 
		};
		if (1 == 0)
		{
		}
		string value2 = text2;
		return $"{IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc)} | {value} | {value2} | {record.X},{record.Y},{record.Z} | {IngameTimeFormatter.LocalizeStoredStamp(record.IngameDate, languageCode)}";
	}

	private static string FormatLine(LogEventRecord record, BlockPos target, string? languageCode)
	{
		string value = (string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : record.PlayerName);
		int value2 = record.X - target.X;
		int value3 = record.Z - target.Z;
		string text = ((!string.IsNullOrWhiteSpace(record.BlockCode)) ? record.BlockCode : (Meta(record, "entityCode") ?? "unknown"));
		LogActionType action = record.Action;
		if (1 == 0)
		{
		}
		string text2 = action switch
		{
			LogActionType.ContainerTake => $"took {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} from {text}", 
			LogActionType.ContainerPut => $"put {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} into {text}", 
			LogActionType.ContainerOpen => "opened " + text, 
			LogActionType.BlockBreak => "broke " + text, 
			LogActionType.BlockPlace => "placed " + text, 
			LogActionType.BlockUse => "used " + text, 
			LogActionType.EntityInteract => "interacted with " + (Meta(record, "entityCode") ?? "entity"), 
			LogActionType.EntitySpawn => "spawned " + (Meta(record, "entityCode") ?? "entity") + CountSuffix(record), 
			LogActionType.EntityDeath => (Meta(record, "entityCode") ?? "entity") + " died", 
			LogActionType.EntityDespawn => "despawned " + (Meta(record, "entityCode") ?? "entity") + CountSuffix(record), 
			_ => record.Action.ToString(), 
		};
		if (1 == 0)
		{
		}
		string value4 = text2;
		return $"{IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc)} | {value} | {value4} | {record.X},{record.Y},{record.Z} | ΔX/Z {value2:+#;-#;0}/{value3:+#;-#;0} | {IngameTimeFormatter.LocalizeStoredStamp(record.IngameDate, languageCode)}";
	}

	private static string? Meta(LogEventRecord record, string key)
	{
		string value;
		return (record.Meta != null && record.Meta.TryGetValue(key, out value)) ? value : null;
	}

	private static int Quantity(LogEventRecord record)
	{
		int result;
		return int.TryParse(Meta(record, "quantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
	}

	private static string CountSuffix(LogEventRecord record)
	{
		long result;
		return (long.TryParse(Meta(record, "eventCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 1) ? $" x{result:N0}" : "";
	}
}
