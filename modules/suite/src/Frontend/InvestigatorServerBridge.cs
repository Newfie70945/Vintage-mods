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
using BlockLogger.LoginProtection;
using BlockLogger.Integrations;
using BlockLogger.Models;
using BlockLogger.Protocol;
using BlockLogger.Restarts;
using BlockLogger.Services;
using BlockLogger.ServerHealth;
using BlockLogger.WebDashboard;
using BlockLogger.WebMap;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace BlockLogger.Frontend;

public sealed class InvestigatorServerBridge
{
    public const string ChannelName = "blocklogger-investigator-v2";
    private const string InvestigatorPrivilege = "blocklogger";

    private readonly ICoreServerAPI api;
    private readonly BlockLoggerModule module;
    private readonly ConcurrentDictionary<string, byte> activeQueries = new();
    private readonly ConcurrentDictionary<string, long> mapTeleportLastRequestMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim fingerprintResolutionGate = new(1, 1);
    private IServerNetworkChannel? channel;

    public InvestigatorServerBridge(ICoreServerAPI api, BlockLoggerModule module)
    {
        this.api = api;
        this.module = module;
    }

    public void Register()
    {
        channel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<InvestigatorQueryRequest>()
            .RegisterMessageType<InvestigatorQueryResponse>()
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
        if (channel == null) return;
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

        string operation = (request.Operation ?? "get").Trim();
        bool motdOperation = operation.StartsWith("motd-", StringComparison.OrdinalIgnoreCase)
            || operation.Equals("motd", StringComparison.OrdinalIgnoreCase);

        CommunityMotdControlSnapshot motd;
        DiscordControlSnapshot snapshot;
        if (motdOperation)
        {
            string motdOp = operation.ToLowerInvariant() switch
            {
                "motd-save" => "save",
                "motd-apply" => "apply",
                "motd-reload" => "reload",
                "motd-resetseen" => "resetseen",
                _ => "get"
            };

            motd = CommunityMotdControlBridge.Request(api, motdOp, request.StringValue ?? "");
            // The page is still hosted under Discord Integration, so return a fresh Discord
            // snapshot too. MOTD itself remains an independent optional companion.
            snapshot = DiscordControlBridge.Request(api, "get");
        }
        else
        {
            snapshot = DiscordControlBridge.Request(
                api,
                operation,
                request.SettingKey,
                request.BoolValue,
                request.IntValue,
                request.StringValue,
                request.Target);
            motd = CommunityMotdControlBridge.Request(api, "get");
        }

        channel.SendPacket(new DiscordIntegrationGuiResponse
        {
            RequestId = request.RequestId,
            Success = motdOperation ? motd.Success : snapshot.Success,
            Message = motdOperation ? motd.Message : snapshot.Message,
            Settings = ToDiscordGuiSettings(snapshot, motd)
        }, player);
    }

    private static DiscordIntegrationGuiSettingsPacket ToDiscordGuiSettings(DiscordControlSnapshot s, CommunityMotdControlSnapshot motd) => new()
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
        MotdVersion = motd.Version ?? "",
        MotdConfigJson = motd.ConfigJson ?? "{}",
        MotdMessage = motd.Message ?? ""
    };

    private static DiscordAnnouncementGuiPacket ToDiscordRoute(DiscordAnnouncementSnapshot route) => new()
    {
        Enabled = route.Enabled,
        ChannelId = route.ChannelId,
        Format = route.Format
    };


    private void HandleAdminCommandGui(IServerPlayer player, AdminCommandGuiRequest request)
    {
        if (channel == null) return;

        var admin = module.Admin;
        if (admin == null)
        {
            SendAdminCommandGui(player, request.RequestId, false, request.Operation, "Administration is disabled.", new List<string>());
            return;
        }

        string op = (request.Operation ?? "").Trim().ToLowerInvariant();
        string requiredPrivilege = AdminPrivilegeForOperation(op);
        if (string.IsNullOrWhiteSpace(requiredPrivilege))
        {
            SendAdminCommandGui(player, request.RequestId, false, op, "Unknown admin action.", new List<string>());
            return;
        }
        bool mapTeleportAdminOverride = op == "map-teleport" &&
            (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege(Privilege.root));
        if (!player.HasPrivilege(requiredPrivilege) && !mapTeleportAdminOverride)
        {
            SendAdminCommandGui(player, request.RequestId, false, op, $"You need the {requiredPrivilege} privilege for this action.", new List<string>());
            return;
        }

        if (op == "map-teleport")
        {
            QueueAdminMapTeleport(player, request, admin);
            return;
        }

        string target = (request.Target ?? "").Trim();
        string value = (request.Value ?? "").Trim();
        string reason = (request.Reason ?? "").Trim();
        string actor = player.PlayerName;
        var lines = new List<string>();
        string message;
        bool success = true;

        try
        {
            switch (op)
            {
                case "player-info":
                {
                    var p = admin.GetPlayer(string.IsNullOrWhiteSpace(target) ? player.PlayerName : target);
                    if (p == null) { success = false; message = "Player not found."; break; }
                    message = $"{p.Name} • {(p.Online ? "Online" : "Offline")} • {p.GameMode}";
                    lines.Add($"UID: {p.PlayerUid}");
                    lines.Add($"Role: {p.Role} • Ping: {p.PingMilliseconds} ms • Position: {p.Position}");
                    lines.Add($"Health: {p.Health:0.#}/{p.MaxHealth:0.#} • Warnings: {p.ActiveWarnings} • Muted: {p.Muted} • Banned: {p.Banned} • Frozen: {p.Frozen} • Jailed: {p.Jailed}");
                    lines.Add($"Playtime: {FormatAdminDuration(p.TotalPlaySeconds)} • Session: {FormatAdminDuration(p.SessionSeconds)}");
                    break;
                }
                case "online":
                {
                    var rows = admin.GetOnlinePlayerSummaries();
                    message = $"{rows.Count} player{(rows.Count == 1 ? "" : "s")} online.";
                    foreach (var p in rows.Take(100))
                        lines.Add($"ADMINPLAYER\t{GuiField(p.PlayerUid)}\t{GuiField(p.Name)}\t{p.PingMilliseconds}\t{GuiField(p.GameMode.ToString())}\t{GuiField(p.Position)}\t{p.Health:0.#}/{p.MaxHealth:0.#}");
                    break;
                }
                case "seen":
                {
                    var p = admin.GetPlayer(target);
                    if (p == null) { success = false; message = "Player not found."; break; }
                    message = p.Online ? $"{p.Name} is online now." : $"{p.Name} was last seen {FormatAdminTime(p.LastSeenUnixMilliseconds)}.";
                    break;
                }
                case "playtime":
                {
                    var p = admin.GetPlayer(target);
                    if (p == null) { success = false; message = "Player not found."; break; }
                    message = $"{p.Name}: {FormatAdminDuration(p.TotalPlaySeconds)} total • {FormatAdminDuration(p.SessionSeconds)} this session.";
                    break;
                }
                case "afk":
                {
                    var p = admin.GetPlayer(target);
                    if (p == null) { success = false; message = "Player not found."; break; }
                    message = $"{p.Name}: {(p.Afk ? "AFK" : "Active")} • idle {FormatAdminDuration(p.IdleSeconds)}.";
                    break;
                }
                case "history":
                {
                    var rows = admin.GetHistory(string.IsNullOrWhiteSpace(target) ? player.PlayerName : target, 80);
                    message = rows.Count == 0 ? "No admin history found." : $"Showing {rows.Count} recent admin history entries.";
                    foreach (var row in rows)
                        lines.Add($"{FormatAdminTime(row.UnixMilliseconds)} • {row.Action} • {row.Actor} • {row.Detail}");
                    break;
                }
                case "warnings":
                {
                    var rows = admin.GetWarnings(target);
                    message = rows.Count == 0 ? "No warnings found." : $"Showing {Math.Min(rows.Count, 50)} warning records.";
                    foreach (var row in rows.Take(50))
                        lines.Add($"#{row.Id} • {(row.Removed ? "REMOVED" : "ACTIVE")} • {FormatAdminTime(row.UnixMilliseconds)} • {row.IssuedBy}: {row.Reason}");
                    break;
                }
                case "notes":
                {
                    var rows = admin.GetNotes(target);
                    message = rows.Count == 0 ? "No staff notes found." : $"Showing {Math.Min(rows.Count, 50)} staff notes.";
                    foreach (var row in rows.Take(50))
                        lines.Add($"#{row.Id} • {(row.Removed ? "REMOVED" : "ACTIVE")} • {FormatAdminTime(row.UnixMilliseconds)} • {row.Author}: {row.Text}");
                    break;
                }
                case "heal": message = admin.Heal(actor, target, "shiftl"); break;
                case "feed": message = admin.Feed(actor, target, "shiftl"); break;
                case "revive": message = admin.Revive(actor, target, "shiftl"); break;
                case "smite": message = admin.Smite(actor, target, reason, "shiftl"); break;
                case "setjail": message = admin.SetJailPosition(player, "shiftl"); break;
                case "jail": message = admin.Jail(actor, target, string.IsNullOrWhiteSpace(value) ? "30m" : value, reason, "shiftl"); break;
                case "unjail": message = admin.Unjail(actor, target, reason, "shiftl"); break;
                case "jailinfo": message = admin.JailInfo(target); break;
                case "ghost": message = admin.ToggleGhost(player, "shiftl"); break;
                case "warn": message = admin.Warn(actor, target, reason, "shiftl"); break;
                case "unwarn": message = admin.RemoveWarning(actor, target, request.LongValue, "shiftl"); break;
                case "mute": message = admin.Mute(actor, target, string.IsNullOrWhiteSpace(value) ? "30m" : value, reason, "shiftl"); break;
                case "unmute": message = admin.Unmute(actor, target, "shiftl"); break;
                case "muteinfo":
                {
                    var p = admin.GetPlayer(target);
                    if (p == null) { success = false; message = "Player not found."; break; }
                    message = p.Muted
                        ? $"{p.Name} is muted{(p.MuteUntilUnixMilliseconds > 0 ? $" until {FormatAdminTime(p.MuteUntilUnixMilliseconds)}" : " permanently")}."
                        : $"{p.Name} is not muted.";
                    break;
                }
                case "tempban": message = admin.TempBan(actor, target, string.IsNullOrWhiteSpace(value) ? "1d" : value, reason, "shiftl"); break;
                case "unban": message = admin.Unban(actor, target, "shiftl"); break;
                case "baninfo":
                {
                    var p = admin.GetPlayer(target);
                    if (p == null) { success = false; message = "Player not found."; break; }
                    message = p.Banned ? $"{p.Name} is temporarily banned until {FormatAdminTime(p.BanUntilUnixMilliseconds)}." : $"{p.Name} has no active suite temporary ban.";
                    break;
                }
                case "freeze": message = admin.Freeze(actor, target, string.IsNullOrWhiteSpace(value) ? "permanent" : value, reason, "shiftl"); break;
                case "unfreeze": message = admin.Unfreeze(actor, target, "shiftl"); break;
                case "kick": message = admin.Kick(actor, target, reason, "shiftl"); break;
                case "note-add": message = admin.AddNote(actor, target, reason, "shiftl"); break;
                case "note-remove": message = admin.RemoveNote(actor, target, request.LongValue, "shiftl"); break;

                case "goto": message = admin.Goto(player, target); break;
                case "bring": message = admin.Bring(player, target); break;
                case "back": message = admin.Back(player); break;
                case "sendback": message = admin.SendBack(actor, target, "shiftl"); break;

                case "invsee":
                {
                    var all = admin.GetInventory(target);
                    var occupied = all.Where(x => !x.Empty).ToList();
                    if (all.Count == 0) { success = false; message = "Player must be online or inventory is unavailable."; break; }
                    message = $"Inventory for {target}: {occupied.Count} occupied / {all.Count} slots. Click a slot in the visual inventory to edit it.";
                    AppendAdminInventoryGuiLines(admin, target, lines);
                    break;
                }
                case "itemsearch":
                {
                    var matches = admin.SearchItems(value, 80);
                    message = matches.Count == 0
                        ? $"No items matched '{value}'."
                        : $"Item search for '{value}' • {matches.Count} match{(matches.Count == 1 ? "" : "es")}. Select an item to copy its code into the editor.";
                    foreach (var item in matches)
                        lines.Add($"ITEM\t{GuiField(item.Code)}\t{GuiField(item.Name)}\t{GuiField(item.ItemClass)}\t{item.MaxStackSize}");
                    break;
                }
                case "invsnapshot": message = admin.CreateInventorySnapshot(actor, target, string.IsNullOrWhiteSpace(reason) ? "Shift+L snapshot" : reason, "shiftl"); break;
                case "invgive": message = admin.GiveInventoryItem(actor, target, value, Math.Max(1, request.IntValue), source: "shiftl"); break;
                case "invgive-selected": message = admin.GiveInventoryItem(actor, target, value, Math.Max(1, request.IntValue), request.Secondary, request.IntValue2, request.ExpectedFingerprint, "shiftl"); break;
                case "invqty": message = admin.SetInventoryQuantity(actor, target, value, request.IntValue, request.IntValue2, request.ExpectedFingerprint, "shiftl"); break;
                case "invreplace": message = admin.ReplaceInventorySlot(actor, target, request.Secondary, request.IntValue, value, request.IntValue2, request.ExpectedFingerprint, "shiftl"); break;
                case "invremove":
                    if (!request.Confirmed) { success = false; message = "Confirmation required before removing an inventory slot."; }
                    else message = admin.RemoveInventorySlot(actor, target, value, request.IntValue, "shiftl", request.ExpectedFingerprint);
                    break;
                case "invrestore":
                    if (!request.Confirmed) { success = false; message = "Confirmation required before restoring a snapshot."; }
                    else message = admin.RestoreInventorySnapshot(actor, target, request.LongValue, "shiftl");
                    break;

                case "laghere":
                {
                    var r = admin.ScanLagHere(player, Math.Clamp(request.IntValue <= 0 ? 3 : request.IntValue, 1, 16));
                    message = $"Lag scan: {r.Entities} entities • {r.DroppedItems} drops • {r.LoadedChunks} chunks.";
                    lines.Add($"Radius: {r.RadiusChunks} chunks • Main-thread delay: {r.MainThreadDelayMs:0.#} ms • CPU: {r.CpuPercent:0.#}% • BlockLogger queue: {r.BlockLoggerQueue}");
                    foreach (var kv in r.TopEntityTypes.OrderByDescending(x => x.Value).Take(20)) lines.Add($"{kv.Key} • {kv.Value}");
                    break;
                }
                case "cleanup-scan":
                {
                    var r = admin.ScanCleanup();
                    message = $"Cleanup scan: {r.LoadedEntities} loaded entities • {r.DroppedItems} dropped items.";
                    lines.Add($"Creatures {r.Creatures} • Inanimate {r.InanimateEntities} • Dead/despawning {r.DeadOrDespawning}");
                    break;
                }
                case "cleanup-drops":
                    if (!request.Confirmed) { success = false; message = "Confirmation required before removing dropped items."; }
                    else
                    {
                        var r = admin.CleanupDrops(false);
                        message = $"Removed {r.Removed} dropped item entities.";
                    }
                    break;
                case "maintenance-on":
                {
                    long durationMs = 0;
                    if (!string.IsNullOrWhiteSpace(value) && !AdminService.TryParseDuration(value, out durationMs, out bool permanent))
                    {
                        success = false; message = "Invalid duration. Examples: 30m, 6h, 2d."; break;
                    }
                    message = admin.SetMaintenance(actor, true, string.IsNullOrWhiteSpace(reason) ? "Maintenance" : reason, durationMs, "shiftl");
                    break;
                }
                case "maintenance-off": message = admin.SetMaintenance(actor, false, reason, 0, "shiftl"); break;
                case "maintenance-status":
                {
                    var m = admin.GetMaintenance();
                    message = m.Enabled ? $"Maintenance ON • {m.Reason}{(m.Until > 0 ? $" • until {FormatAdminTime(m.Until)}" : "")}" : "Maintenance is off.";
                    break;
                }
                case "shutdown":
                case "restart":
                {
                    if (!request.Confirmed) { success = false; message = "Confirmation required before scheduling a shutdown/restart."; break; }
                    if (!AdminService.TryParseDuration(string.IsNullOrWhiteSpace(value) ? "5m" : value, out long delayMs, out bool permanent) || permanent)
                    {
                        success = false; message = "Invalid delay. Examples: 1m, 5m, 30m."; break;
                    }
                    message = op == "restart"
                        ? admin.ScheduleRestart(actor, delayMs, reason, "shiftl")
                        : admin.ScheduleShutdown(actor, delayMs, reason, "shiftl");
                    break;
                }
                case "shutdown-cancel": message = admin.CancelShutdown(actor, "shiftl"); break;
                case "autorestart-status":
                {
                    var restarts = module.AutomatedRestarts;
                    if (restarts == null) { success = false; message = "Automated restarts are unavailable."; break; }
                    message = restarts.StatusText();
                    AppendAutoRestartGuiLines(restarts, lines);
                    break;
                }
                case "autorestart-enable":
                case "autorestart-disable":
                {
                    var restarts = module.AutomatedRestarts;
                    if (restarts == null) { success = false; message = "Automated restarts are unavailable."; break; }
                    message = restarts.SetEnabled(op == "autorestart-enable");
                    AppendAutoRestartGuiLines(restarts, lines);
                    break;
                }
                case "autorestart-reload":
                {
                    var restarts = module.AutomatedRestarts;
                    if (restarts == null) { success = false; message = "Automated restarts are unavailable."; break; }
                    message = restarts.Reload();
                    AppendAutoRestartGuiLines(restarts, lines);
                    break;
                }
                case "autorestart-skip":
                {
                    var restarts = module.AutomatedRestarts;
                    if (restarts == null) { success = false; message = "Automated restarts are unavailable."; break; }
                    if (!request.Confirmed) { success = false; message = "Confirmation required before skipping the next scheduled restart."; break; }
                    message = restarts.SkipNext(actor);
                    AppendAutoRestartGuiLines(restarts, lines);
                    break;
                }
                case "autorestart-save":
                {
                    var restarts = module.AutomatedRestarts;
                    if (restarts == null) { success = false; message = "Automated restarts are unavailable."; break; }
                    var timeValues = SplitCsv(value);
                    var warningValues = SplitCsv(request.Secondary)
                        .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : -1)
                        .Where(x => x > 0)
                        .ToList();
                    if (timeValues.Count == 0) { success = false; message = "Enter at least one UTC restart time (HH:mm or HH:mm:ss)."; break; }
                    string primary = timeValues[0];
                    var multiple = timeValues.Count > 1 ? timeValues : new List<string>();
                    var save = restarts.SaveSettings(new AutomatedRestartSettingsRequest
                    {
                        Enabled = restarts.Config.Enabled,
                        RestartTimeUtc = primary,
                        RestartTimesUtc = multiple,
                        WarningMinutes = warningValues,
                        AnnounceInGame = (request.IntValue & 1) != 0,
                        AnnounceToDiscord = (request.IntValue & 2) != 0,
                        Reason = reason
                    });
                    success = save.Success;
                    message = save.Message;
                    AppendAutoRestartGuiLines(restarts, lines);
                    break;
                }
                case "chatdisplay-status":
                {
                    var chat = module.ChatDisplay;
                    if (chat == null) { success = false; message = "Chat display service is unavailable."; break; }
                    message = "Chat display status loaded.";
                    AppendChatDisplayGuiLines(chat, lines);
                    break;
                }
                case "chatdisplay-save":
                {
                    var chat = module.ChatDisplay;
                    if (chat == null) { success = false; message = "Chat display service is unavailable."; break; }
                    var save = chat.SaveSettings(new ChatDisplaySettingsRequest
                    {
                        ShowRoleBeforeName = (request.IntValue & 1) != 0,
                        BoldPlayerName = (request.IntValue & 8) != 0,
                        ShowTimestampBeforeMessage = (request.IntValue & 2) != 0,
                        UseUtcTimestamp = (request.IntValue & 4) != 0,
                        FormatGeneralChat = (request.IntValue & 16) != 0,
                        FormatOtherChatGroups = (request.IntValue & 32) != 0,
                        AdditionalFormattedChatGroupIds = SplitCsv(request.Secondary)
                            .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : -1)
                            .Where(id => id >= 0)
                            .Distinct()
                            .ToList(),
                        TimestampFormat = string.IsNullOrWhiteSpace(value) ? "HH:mm" : value
                    });
                    success = save.Success;
                    message = save.Message;
                    AppendChatDisplayGuiLines(chat, lines);
                    break;
                }
                case "undo": message = admin.Undo(actor, "shiftl"); break;

                case "reports-open":
                case "reports-all":
                {
                    var reports = module.Reports;
                    if (reports == null) { success = false; message = "Reports feature is disabled."; break; }
                    var snap = reports.GetSnapshot(op == "reports-open" ? "open" : "all", 100);
                    message = $"Reports: {snap.OpenCount} open • {snap.InvestigatingCount} investigating • {snap.ResolvedCount} resolved.";
                    foreach (var r in snap.Reports.Take(100))
                        lines.Add($"#{r.Id} • {r.Status} • by {r.ReporterName} • {r.Reason} • assigned {(!string.IsNullOrWhiteSpace(r.AssignedStaff) ? r.AssignedStaff : "nobody")}");
                    break;
                }
                case "report-status":
                {
                    var reports = module.Reports;
                    if (reports == null) { success = false; message = "Reports feature is disabled."; break; }
                    message = reports.SetStatus(request.LongValue, value, actor, reason);
                    break;
                }
                case "report-assign":
                {
                    var reports = module.Reports;
                    if (reports == null) { success = false; message = "Reports feature is disabled."; break; }
                    message = reports.Assign(request.LongValue, string.IsNullOrWhiteSpace(value) ? actor : value, actor);
                    break;
                }
                case "report-note":
                {
                    var reports = module.Reports;
                    if (reports == null) { success = false; message = "Reports feature is disabled."; break; }
                    message = reports.AddNote(request.LongValue, actor, reason);
                    break;
                }
                default:
                    success = false;
                    message = "Unknown admin action.";
                    break;
            }
        }
        catch (Exception ex)
        {
            success = false;
            message = $"Admin action failed: {ex.Message}";
            api.Logger.Warning("[ServerEssentials/AdminGUI] {0} failed for {1}: {2}", op, player.PlayerName, ex);
        }

        if (success && LooksLikeAdminFailure(message)) success = false;
        if (success && (op is "invgive" or "invgive-selected" or "invqty" or "invreplace" or "invremove" or "invrestore"))
            AppendAdminInventoryGuiLines(admin, target, lines);
        SendAdminCommandGui(player, request.RequestId, success, op, message, lines);
    }

    private static void AppendAutoRestartGuiLines(AutomatedRestartService restarts, List<string> lines)
    {
        var snapshot = restarts.GetSnapshot();
        lines.Add($"AUTORESTART\t{(snapshot.Enabled ? 1 : 0)}\t{GuiField(string.Join(",", snapshot.EffectiveRestartTimesUtc))}\t{GuiField(string.Join(",", snapshot.WarningMinutes))}\t{GuiField(snapshot.Reason)}\t{(snapshot.AnnounceInGame ? 1 : 0)}\t{(snapshot.AnnounceToDiscord ? 1 : 0)}\t{snapshot.NextRestartUnixMilliseconds}");
        lines.Add($"Root /restarttime provider: {GuiField(snapshot.RootRestartTimeOwner)} • Fallback: {snapshot.FallbackCommand}");
        if (!string.IsNullOrWhiteSpace(snapshot.NextRestartUtc)) lines.Add($"Next UTC restart: {snapshot.NextRestartUtc}");
    }

    private static void AppendChatDisplayGuiLines(ChatDisplayService chat, List<string> lines)
    {
        var snapshot = chat.GetSnapshot();
        lines.Add($"CHATDISPLAY\t{(snapshot.ShowRoleBeforeName ? 1 : 0)}\t{(snapshot.BoldPlayerName ? 1 : 0)}\t{(snapshot.ShowTimestampBeforeMessage ? 1 : 0)}\t{(snapshot.UseUtcTimestamp ? 1 : 0)}\t{GuiField(snapshot.TimestampFormat)}\t{GuiField(snapshot.Preview)}\t{(snapshot.FormatGeneralChat ? 1 : 0)}\t{(snapshot.FormatOtherChatGroups ? 1 : 0)}\t{GuiField(string.Join(",", snapshot.AdditionalFormattedChatGroupIds))}");
    }

    private static List<string> SplitCsv(string? value) => (value ?? "")
        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToList();

    private static void AppendAdminInventoryGuiLines(AdminService admin, string target, List<string> lines)
    {
        if (admin == null || lines == null || string.IsNullOrWhiteSpace(target)) return;
        foreach (var slot in admin.GetInventory(target))
        {
            lines.Add(
                $"INV\t{GuiField(slot.Inventory)}\t{GuiField(slot.Section)}\t{slot.Slot}\t{(slot.Empty ? 1 : 0)}\t{slot.StackSize}\t{slot.MaxStackSize}\t{GuiField(slot.Code)}\t{GuiField(slot.Name)}\t{slot.Durability}\t{slot.MaxDurability}\t{slot.Temperature.ToString(CultureInfo.InvariantCulture)}\t{GuiField(slot.StorageType)}\t{GuiField(slot.Fingerprint)}\t{GuiField(slot.SerializedStack)}");
        }
    }

    private static string AdminPrivilegeForOperation(string op) => op switch
    {
        "player-info" or "online" or "seen" or "playtime" or "afk" or "history" => AdminService.PrivView,
        "warnings" or "warn" or "unwarn" => AdminService.PrivWarn,
        "notes" or "note-add" or "note-remove" or "heal" or "feed" or "revive" or "ghost" or "freeze" or "unfreeze" or "kick" => AdminService.PrivManage,
        "smite" => AdminService.PrivSmite,
        "setjail" or "jail" or "unjail" or "jailinfo" => AdminService.PrivJail,
        "mute" or "unmute" or "muteinfo" => AdminService.PrivMute,
        "tempban" or "unban" or "baninfo" => AdminService.PrivBan,
        "goto" or "bring" or "back" or "sendback" or "map-teleport" => AdminService.PrivTeleport,
        "invsee" or "itemsearch" => AdminService.PrivInventory,
        "invsnapshot" or "invgive" or "invgive-selected" or "invqty" or "invreplace" or "invremove" or "invrestore" => AdminService.PrivInventoryEdit,
        "laghere" => AdminService.PrivDiagnostics,
        "cleanup-scan" or "cleanup-drops" => AdminService.PrivCleanup,
        "maintenance-on" or "maintenance-off" or "maintenance-status" or "shutdown" or "restart" or "shutdown-cancel" or
        "autorestart-status" or "autorestart-enable" or "autorestart-disable" or "autorestart-reload" or "autorestart-skip" or "autorestart-save" or
        "chatdisplay-status" or "chatdisplay-save" => AdminService.PrivServer,
        "undo" => AdminService.PrivManage,
        "reports-open" or "reports-all" or "report-status" or "report-assign" or "report-note" => AdminService.PrivReports,
        _ => ""
    };

    private void QueueAdminMapTeleport(IServerPlayer player, AdminCommandGuiRequest request, AdminService admin)
    {
        int x = request.IntValue;
        int z = request.IntValue2;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (mapTeleportLastRequestMs.TryGetValue(player.PlayerUID, out long previous) && now - previous < 750)
        {
            SendAdminCommandGui(player, request.RequestId, false, "map-teleport", "Map teleport is cooling down for a moment.", new List<string>());
            return;
        }
        mapTeleportLastRequestMs[player.PlayerUID] = now;

        var accessor = api.World.BlockAccessor;
        if (x < 0 || z < 0 || x >= accessor.MapSizeX || z >= accessor.MapSizeZ)
        {
            SendAdminCommandGui(player, request.RequestId, false, "map-teleport", "That map position is outside the world bounds.", new List<string>());
            return;
        }

        int dimension = 0;
        if (EntityPositionResolver.TryGetBlockPos(player.Entity, out var currentPos))
            dimension = currentPos.dimension;

        // The normal world map's terrain/rain height maps are authoritative for dimension 0.
        // Refuse other dimensions rather than guessing a landing height and risking suffocation.
        if (dimension != 0)
        {
            SendAdminCommandGui(player, request.RequestId, false, "map-teleport", "World-map teleport currently supports the overworld dimension only.", new List<string>());
            return;
        }

        int chunkX = (int)Math.Floor(x / (double)GlobalConstants.ChunkSize);
        int chunkZ = (int)Math.Floor(z / (double)GlobalConstants.ChunkSize);

        try
        {
            api.WorldManager.LoadChunkColumnPriority(
                chunkX,
                chunkZ,
                new ChunkLoadOptions
                {
                    KeepLoaded = false,
                    OnLoaded = () => api.Event.EnqueueMainThreadTask(() =>
                    {
                        try
                        {
                            if (player.Entity == null) return;

                            var safe = FindSafeMapTeleportPosition(x, z, dimension);
                            string message = admin.MapTeleport(player, safe, "worldmap");
                            SendAdminCommandGui(player, request.RequestId, true, "map-teleport", message, new List<string>());
                        }
                        catch (Exception ex)
                        {
                            api.Logger.Warning("[ServerEssentials/AdminMapTeleport] Completion failed for {0}: {1}", player.PlayerName, ex);
                            SendAdminCommandGui(player, request.RequestId, false, "map-teleport", "Map teleport failed while resolving a safe landing position.", new List<string>());
                        }
                    }, "nses-admin-map-teleport")
                });
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerEssentials/AdminMapTeleport] Chunk load failed for {0}: {1}", player.PlayerName, ex);
            SendAdminCommandGui(player, request.RequestId, false, "map-teleport", "Unable to load the selected map area.", new List<string>());
        }
    }

    private BlockPos FindSafeMapTeleportPosition(int x, int z, int dimension)
    {
        var accessor = api.World.BlockAccessor;
        int maxY = Math.Max(3, accessor.MapSizeY - 3);
        int surfaceY = Math.Clamp(accessor.GetRainMapHeightAt(x, z), 1, maxY - 1);
        int baseY = Math.Clamp(surfaceY + 1, 1, maxY);

        // Prefer a two-block-tall traversable space with supporting ground, starting
        // at the current rain-height surface and searching a modest vertical band.
        for (int offset = 0; offset <= 24; offset++)
        {
            int upY = baseY + offset;
            if (upY <= maxY && IsSafeStandingPosition(x, upY, z, dimension))
                return new BlockPos(x, upY, z, dimension);

            if (offset == 0) continue;
            int downY = baseY - offset;
            if (downY >= 1 && IsSafeStandingPosition(x, downY, z, dimension))
                return new BlockPos(x, downY, z, dimension);
        }

        // Fallback stays above the current rain-height surface. This is preferable to
        // guessing underground if a modded block has unusual traversability rules.
        return new BlockPos(x, Math.Clamp(baseY + 1, 1, maxY), z, dimension);
    }

    private bool IsSafeStandingPosition(int x, int y, int z, int dimension)
    {
        var accessor = api.World.BlockAccessor;
        var feet = new BlockPos(x, y, z, dimension);
        var head = new BlockPos(x, y + 1, z, dimension);
        var ground = new BlockPos(x, y - 1, z, dimension);

        return !accessor.IsNotTraversable(feet) &&
               !accessor.IsNotTraversable(head) &&
               accessor.IsNotTraversable(ground);
    }

    private void SendAdminCommandGui(IServerPlayer player, long requestId, bool success, string operation, string message, List<string> lines)
    {
        channel?.SendPacket(new AdminCommandGuiResponse
        {
            RequestId = requestId,
            Success = success,
            Operation = operation ?? "",
            Message = message ?? "",
            Lines = lines ?? new List<string>()
        }, player);
    }

    private static bool LooksLikeAdminFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        string m = message.ToLowerInvariant();
        return m.Contains("not found") || m.Contains("invalid") || m.Contains("unavailable") ||
               m.Contains("failed") || m.Contains("must be online") || m.StartsWith("usage:") ||
               m.Contains("required") || m.Contains("unknown admin action");
    }

    private static string FormatAdminTime(long unixMs) => unixMs <= 0
        ? "never"
        : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("u", CultureInfo.InvariantCulture);

    private static string FormatAdminDuration(long seconds)
    {
        if (seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    private void HandleBlockLoggerSettings(IServerPlayer player, BlockLoggerSettingsRequest request)
    {
        if (channel == null) return;

        string op = (request.Operation ?? "get").Trim().ToLowerInvariant();

        if (op == "get")
        {
            if (!player.HasPrivilege(InvestigatorPrivilege))
            {
                SendBlockLoggerSettings(player, request.RequestId, false, "You do not have the blocklogger privilege.");
                return;
            }
        }
        else if (!player.HasPrivilege("controlserver"))
        {
            SendBlockLoggerSettings(player, request.RequestId, false, "You need the controlserver privilege to change BlockLogger settings.");
            return;
        }

        try
        {
            var config = module.Config;
            string key = (request.SettingKey ?? "").Trim().ToLowerInvariant();

            if (op == "setbool")
            {
                switch (key)
                {
                    case "enabled": config.Enabled = request.BoolValue; break;
                    case "logblockbreak": config.LogBlockBreak = request.BoolValue; break;
                    case "logblockplace": config.LogBlockPlace = request.BoolValue; break;
                    case "logblockuse": config.LogBlockUse = request.BoolValue; break;
                    case "logcontaineraccess": config.LogContainerAccess = request.BoolValue; break;
                    case "logcontaineritemchanges": config.LogContainerItemChanges = request.BoolValue; break;
                    case "logentityinteract": config.LogEntityInteract = request.BoolValue; break;
                    case "logentityspawn": config.LogEntitySpawn = request.BoolValue; break;
                    case "logentitydeath": config.LogEntityDeath = request.BoolValue; break;
                    case "logentitydespawn": config.LogEntityDespawn = request.BoolValue; break;
                    case "aggregateentityspawndespawn": config.AggregateEntitySpawnDespawn = request.BoolValue; break;
                    default:
                        SendBlockLoggerSettings(player, request.RequestId, false, $"Unknown BlockLogger setting '{request.SettingKey}'.");
                        return;
                }

                module.SaveRuntimeConfig();
            }
            else if (op == "setint")
            {
                switch (key)
                {
                    case "retentiondays": config.RetentionDays = request.IntValue; break;
                    case "flushintervalms": config.FlushIntervalMs = request.IntValue; break;
                    case "maxqueuedevents": config.MaxQueuedEvents = request.IntValue; break;
                    case "emergencyoverflowcapacity": config.EmergencyOverflowCapacity = request.IntValue; break;
                    case "defaultradius": config.DefaultRadius = request.IntValue; break;
                    case "entityradius": config.EntityRadius = request.IntValue; break;
                    case "maxradius": config.MaxRadius = request.IntValue; break;
                    case "defaultdisplaycount": config.DefaultDisplayCount = request.IntValue; break;
                    case "defaultentitydisplaycount": config.DefaultEntityDisplayCount = request.IntValue; break;
                    case "maxdisplaycount": config.MaxDisplayCount = request.IntValue; break;
                    default:
                        SendBlockLoggerSettings(player, request.RequestId, false, $"Unknown BlockLogger setting '{request.SettingKey}'.");
                        return;
                }

                module.SaveRuntimeConfig();
            }
            else if (op != "get")
            {
                SendBlockLoggerSettings(player, request.RequestId, false, $"Unknown BlockLogger settings operation '{request.Operation}'.");
                return;
            }

            SendBlockLoggerSettings(
                player,
                request.RequestId,
                true,
                op == "get" ? "BlockLogger settings" : "BlockLogger setting saved.");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger] GUI settings request failed: {0}: {1}", ex.GetType().Name, ex.Message);
            SendBlockLoggerSettings(player, request.RequestId, false, "BlockLogger settings update failed. Check the server log.");
        }
    }

    private void SendBlockLoggerSettings(IServerPlayer player, long requestId, bool success, string message)
    {
        if (channel == null) return;
        var config = module.Config;

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

    private void HandleAnimalLimiter(IServerPlayer player, AnimalLimiterRequest request)
    {
        if (channel == null) return;
        if (!player.HasPrivilege("controlserver"))
        {
            channel.SendPacket(new AnimalLimiterResponse
            {
                RequestId = request.RequestId,
                Message = "You need the controlserver privilege to change the animal limiter."
            }, player);
            return;
        }

        var limiter = AnimalLimiterSystem.Current;
        if (limiter == null)
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
            string op = (request.Operation ?? "status").Trim().ToLowerInvariant();
            int limit = Math.Clamp(request.Limit, 0, 1000000);
            string message = op switch
            {
                "target" => limiter.SetTargetRule(player, limit, request.RadiusChunks),
                "set" => limiter.SetRuleFromGui(request.Pattern, limit, request.RadiusChunks),
                "remove" => limiter.RemoveRuleFromGui(request.Pattern),
                "preview" => limiter.PreviewFromGui(request.Pattern, string.IsNullOrWhiteSpace(request.Pattern) ? null : limit, request.RadiusChunks),
                "cull" => limiter.CullFromGui(),
                "setradius" => limiter.SetRadiusFromGui(request.RadiusChunks),
                "setint" => limiter.SetIntegerSettingFromGui(request.SettingKey, request.IntValue),
                "setbool" => limiter.SetBooleanSettingFromGui(request.SettingKey, request.BoolValue),
                "history" => "Animal Limiter removal logs",
                "population" => "Animal Limiter population status",
                "rules" => "Animal Limiter rules",
                "settings" => "Animal Limiter settings",
                _ => "Animal Limiter status"
            };

            var lines = op switch
            {
                "history" => limiter.GetGuiRemovalLogLines(75),
                "population" => limiter.GetGuiPopulationLines(),
                "rules" => limiter.GetGuiRuleLines(),
                "settings" or "setradius" or "setint" or "setbool" => limiter.GetGuiSettingsLines(),
                _ => limiter.GetGuiStatusLines()
            };

            channel.SendPacket(new AnimalLimiterResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Message = message,
                Lines = lines,
                Enabled = limiter.GuiEnabled,
                RadiusChunks = limiter.GuiRadiusChunks,
                DefaultPerEntityLimit = limiter.GuiDefaultPerEntityLimit,
                PlayerProtectionRadiusBlocks = limiter.GuiPlayerProtectionRadiusBlocks,
                ReconciliationSeconds = limiter.GuiReconciliationSeconds,
                LimitAllEntities = limiter.GuiLimitAllEntities,
                ProtectPets = limiter.GuiProtectPets,
                ProtectFarmAnimals = limiter.GuiProtectFarmAnimals,
                ProtectNamedEntities = limiter.GuiProtectNamedEntities,
                KeepNearestToPlayers = limiter.GuiKeepNearestToPlayers,
                LogActions = limiter.GuiLogActions,
                LogToCsv = limiter.GuiLogToCsv,
                GenerationFilter = limiter.GuiGenerationFilter
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
        if (channel == null) return;

        string op = (request.Operation ?? "overview").Trim().ToLowerInvariant();
        bool rescan = op == "rescan";
        bool setEnabled = op == "setenabled";
        bool blacklistWrite = op is "blacklistadd" or "blacklistremove" or "blacklistresolve";
        bool settingsWrite = op is "settingssave" or "packetsave";
        bool enforcementWrite = op == "enforcementsave";
        if (rescan || setEnabled || blacklistWrite || settingsWrite || enforcementWrite)
        {
            if (!player.HasPrivilege("controlserver"))
            {
                string denied = setEnabled
                    ? "You need the controlserver privilege to enable or disable Anti-Cheat."
                    : blacklistWrite
                        ? "You need the controlserver privilege to change the Anti-Cheat mod blacklist."
                        : settingsWrite
                            ? "You need the controlserver privilege to change Anti-Cheat settings."
                            : enforcementWrite
                                ? "You need the controlserver privilege to change Anti-Cheat enforcement rules."
                                : "You need the controlserver privilege to request an anti-cheat rescan.";
                SendAntiCheatGuiResponse(player, request.RequestId, false, op, denied, new List<string>(), request.Search);
                return;
            }
        }
        else if (!player.HasPrivilege(InvestigatorPrivilege))
        {
            SendAntiCheatGuiResponse(player, request.RequestId, false, op,
                "You do not have the blocklogger privilege.", new List<string>(), request.Search);
            return;
        }

        var antiCheat = module.AntiCheat;
        if (antiCheat == null)
        {
            SendAntiCheatGuiResponse(player, request.RequestId, false, op,
                "Anti-cheat service is not available.", new List<string>(), request.Search);
            return;
        }

        try
        {
            if (setEnabled)
            {
                var result = antiCheat.SaveModuleSettings(request.BoolValue);
                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, result.Success, "overview",
                    result.Message, BuildAntiCheatOverviewLines(snapshot), "");
                return;
            }

            if (rescan)
            {
                string message = antiCheat.RequestRescan(request.Search ?? "");
                bool success = message.StartsWith("Anti-cheat rescan requested", StringComparison.OrdinalIgnoreCase);
                var detail = antiCheat.GetPlayerSnapshot(request.Search ?? "");
                var recentHistory = detail == null ? null : antiCheat.GetHistorySnapshot(detail.PlayerUid, 12);
                var lines = detail == null ? new List<string>() : BuildAntiCheatPlayerLines(detail, recentHistory);
                SendAntiCheatGuiResponse(player, request.RequestId, success, "player", message, lines, detail?.PlayerName ?? request.Search);
                return;
            }

            if (op == "overview")
            {
                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, true, op,
                    "Anti-Cheat overview", BuildAntiCheatOverviewLines(snapshot), "");
                return;
            }

            if (op == "players")
            {
                var snapshot = antiCheat.GetSnapshot();
                var lines = snapshot.Players
                    .OrderByDescending(x => x.RiskScore)
                    .ThenBy(x => x.PlayerName, StringComparer.OrdinalIgnoreCase)
                    .Select(x =>
                        $"PLAYER\t{x.RiskScore}\t{RiskRating(x.RiskScore)}\t{GuiField(x.Status)}\t{GuiField(x.PlayerName)}\t{GuiField(x.ClientInstalled ? x.ClientVersion : "missing")}\t{x.FindingCount}\t{x.RuntimeNewAssemblyCount}\t{x.ModCount}\t{x.AssemblyCount}\t{GuiField(x.PlayerUid)}")
                    .ToList();
                if (lines.Count == 0) lines.Add("No online anti-cheat sessions.");
                SendAntiCheatGuiResponse(player, request.RequestId, true, op,
                    $"Online players by risk • {snapshot.Suspicious} suspicious • {snapshot.Review} review • {snapshot.Clean} clean", lines, "");
                return;
            }

            if (op == "reports")
            {
                var history = antiCheat.GetHistorySnapshot(request.Search ?? "", 80);
                var lines = BuildAntiCheatReportLines(history);
                string filter = (history.PlayerFilter ?? "").Trim();
                string message = filter.Length == 0
                    ? $"Persistent reports • {history.TotalRecords} total • {history.Last24Hours} in last 24h"
                    : $"Persistent reports for {filter} • showing {history.Incidents.Count} of {history.TotalRecords}";
                SendAntiCheatGuiResponse(player, request.RequestId, true, "reports", message, lines, "");
                return;
            }

            if (op == "enforcement")
            {
                var snapshot = antiCheat.GetSnapshot();
                var rules = BuildAntiCheatConsequencePackets(snapshot.ConsequenceRules);
                SendAntiCheatGuiResponse(player, request.RequestId, true, "enforcement",
                    $"Enforcement rules • {rules.Count(x => x.Enabled)} enabled / {rules.Count} total",
                    BuildAntiCheatEnforcementLines(rules), "", null, rules);
                return;
            }

            if (op == "enforcementsave")
            {
                var incomingRules = (request.ConsequenceRules ?? new List<AntiCheatConsequenceRuleGuiPacket>())
                    .Select(ToAntiCheatConsequenceRule)
                    .ToList();
                var result = antiCheat.SaveConsequenceRules(incomingRules, request.ApplyCurrent);
                var snapshot = antiCheat.GetSnapshot();
                var rules = BuildAntiCheatConsequencePackets(snapshot.ConsequenceRules);
                SendAntiCheatGuiResponse(player, request.RequestId, result.Success, "enforcement",
                    result.Message, BuildAntiCheatEnforcementLines(rules), "", null, rules);
                return;
            }

            if (op == "packet")
            {
                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, true, "packet",
                    "Packet protection settings", BuildAntiCheatPacketProtectionSummary(snapshot), "",
                    null, null, BuildAntiCheatPacketProtectionPacket(snapshot));
                return;
            }

            if (op == "packetsave")
            {
                if (request.PacketSettings == null)
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, "packet",
                        "No packet protection settings were supplied.", new List<string>(), "");
                    return;
                }

                var incomingPacket = request.PacketSettings;
                var result = antiCheat.SaveGuiPacketProtectionSettings(new AntiCheatPacketProtectionGuiSaveRequest
                {
                    PacketGuardEnabled = incomingPacket.PacketGuardEnabled,
                    PlayerWarningEnabled = incomingPacket.PlayerWarningEnabled,
                    PlayerWarningCooldownSeconds = incomingPacket.PlayerWarningCooldownSeconds,
                    AccelerationSampleMinMs = incomingPacket.AccelerationSampleMinMs,
                    AccelerationSmoothingFactor = incomingPacket.AccelerationSmoothingFactor,
                    NoclipEnabled = incomingPacket.NoclipEnabled,
                    NoclipRequiredCollisionSamples = incomingPacket.NoclipRequiredCollisionSamples,
                    NoclipMinPenetrationDepth = incomingPacket.NoclipMinPenetrationDepth,
                    NoclipEndpointMinNetMovement = incomingPacket.NoclipEndpointMinNetMovement,
                    MountAccelerationMinSpeedRatio = incomingPacket.MountAccelerationMinSpeedRatio,
                    DiagnosticContextEnabled = incomingPacket.DiagnosticContextEnabled,
                    DiagnosticSuppressionEnabled = incomingPacket.DiagnosticSuppressionEnabled,
                    DiagnosticSpeedHistorySamples = incomingPacket.DiagnosticSpeedHistorySamples,
                    MovementEnabled = incomingPacket.MovementEnabled,
                    MovementSampleMs = incomingPacket.MovementSampleMs,
                    HardMoveDistance = incomingPacket.HardMoveDistance,
                    MovementConfirmSamples = incomingPacket.MovementConfirmSamples,
                    MovementConfirmWindowMs = incomingPacket.MovementConfirmWindowMs,
                    MovementExtremeMultiplier = incomingPacket.MovementExtremeMultiplier,
                    CorrectionGraceMs = incomingPacket.CorrectionGraceMs,
                    RejectUnauthorizedMovement = incomingPacket.RejectUnauthorizedMovement,
                    ExemptAdmins = incomingPacket.ExemptAdmins,
                    ExemptMountedPlayers = incomingPacket.ExemptMountedPlayers,
                    MountGraceMs = incomingPacket.MountGraceMs,
                    EngineTeleportGraceMs = incomingPacket.EngineTeleportGraceMs,
                    TPRunesCompatibility = incomingPacket.TPRunesCompatibility,
                    TPRunesGraceMs = incomingPacket.TPRunesGraceMs,
                    Th3EssentialsSpawnCompatibility = incomingPacket.Th3EssentialsSpawnCompatibility,
                    Th3EssentialsSpawnGraceMs = incomingPacket.Th3EssentialsSpawnGraceMs,
                    ReachEnabled = incomingPacket.ReachEnabled,
                    ReachTolerance = incomingPacket.ReachTolerance,
                    ReachLatencyCompensation = incomingPacket.ReachLatencyCompensation,
                    ReachMaxLatencyCompensation = incomingPacket.ReachMaxLatencyCompensation,
                    ReachPositionHistoryEnabled = incomingPacket.ReachPositionHistoryEnabled,
                    ReachPositionHistoryMs = incomingPacket.ReachPositionHistoryMs,
                    ReachMaxRewindMs = incomingPacket.ReachMaxRewindMs,
                    ReachUseExtraTolerance = incomingPacket.ReachUseExtraTolerance,
                    ReachBreakExtraTolerance = incomingPacket.ReachBreakExtraTolerance,
                    ReachNearThresholdStrikes = incomingPacket.ReachNearThresholdStrikes,
                    ReachStrikeWindowMs = incomingPacket.ReachStrikeWindowMs,
                    ReachStrikeDecayMs = incomingPacket.ReachStrikeDecayMs,
                    ReachHardExcess = incomingPacket.ReachHardExcess,
                    RejectOutOfReachActions = incomingPacket.RejectOutOfReachActions,
                    MiningEnabled = incomingPacket.MiningEnabled,
                    VeinWindowMs = incomingPacket.VeinWindowMs,
                    ConnectedBreakLimit = incomingPacket.ConnectedBreakLimit,
                    MiningDuplicateTargetMs = incomingPacket.MiningDuplicateTargetMs,
                    RejectConnectedBreakBursts = incomingPacket.RejectConnectedBreakBursts,
                    MiningCadenceValidationEnabled = incomingPacket.MiningCadenceValidationEnabled,
                    MiningCadenceMinExpectedMs = incomingPacket.MiningCadenceMinExpectedMs,
                    MiningCadenceSuspiciousRatio = incomingPacket.MiningCadenceSuspiciousRatio,
                    MiningCadenceConfirmations = incomingPacket.MiningCadenceConfirmations,
                    MiningCadenceConfirmationWindowMs = incomingPacket.MiningCadenceConfirmationWindowMs,
                    RejectImpossibleMiningCadence = incomingPacket.RejectImpossibleMiningCadence,
                    TpsAwarenessEnabled = incomingPacket.TpsAwarenessEnabled,
                    TpsDegradedPercent = incomingPacket.TpsDegradedPercent,
                    TpsCriticalPercent = incomingPacket.TpsCriticalPercent,
                    TpsMaxTimingMultiplier = incomingPacket.TpsMaxTimingMultiplier,
                    TpsSuppressTimingRejectsWhenCritical = incomingPacket.TpsSuppressTimingRejectsWhenCritical,
                    PerFamilyRiskEnabled = incomingPacket.PerFamilyRiskEnabled,
                    MovementRiskDecaySeconds = incomingPacket.MovementRiskDecaySeconds,
                    MovementRiskReviewThreshold = incomingPacket.MovementRiskReviewThreshold,
                    MovementRiskSuspiciousThreshold = incomingPacket.MovementRiskSuspiciousThreshold,
                    ReachRiskDecaySeconds = incomingPacket.ReachRiskDecaySeconds,
                    ReachRiskReviewThreshold = incomingPacket.ReachRiskReviewThreshold,
                    ReachRiskSuspiciousThreshold = incomingPacket.ReachRiskSuspiciousThreshold,
                    MiningRiskDecaySeconds = incomingPacket.MiningRiskDecaySeconds,
                    MiningRiskReviewThreshold = incomingPacket.MiningRiskReviewThreshold,
                    MiningRiskSuspiciousThreshold = incomingPacket.MiningRiskSuspiciousThreshold,
                    PacketAbuseRiskDecaySeconds = incomingPacket.PacketAbuseRiskDecaySeconds,
                    PacketAbuseRiskReviewThreshold = incomingPacket.PacketAbuseRiskReviewThreshold,
                    PacketAbuseRiskSuspiciousThreshold = incomingPacket.PacketAbuseRiskSuspiciousThreshold,
                    ClientIntegrityRiskDecaySeconds = incomingPacket.ClientIntegrityRiskDecaySeconds,
                    ClientIntegrityRiskReviewThreshold = incomingPacket.ClientIntegrityRiskReviewThreshold,
                    ClientIntegrityRiskSuspiciousThreshold = incomingPacket.ClientIntegrityRiskSuspiciousThreshold,
                    IncidentCorrelationEnabled = incomingPacket.IncidentCorrelationEnabled,
                    IncidentCorrelationWindowSeconds = incomingPacket.IncidentCorrelationWindowSeconds,
                    IncidentCorrelationRequiredCategories = incomingPacket.IncidentCorrelationRequiredCategories,
                    IncidentCorrelationMaxRiskBonus = incomingPacket.IncidentCorrelationMaxRiskBonus,
                    HistoryEnabled = incomingPacket.HistoryEnabled,
                    HistoryRetentionDays = incomingPacket.HistoryRetentionDays,
                    HistoryMaxRecords = incomingPacket.HistoryMaxRecords
                });

                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, result.Success, "packet",
                    result.Message, BuildAntiCheatPacketProtectionSummary(snapshot), "",
                    null, null, BuildAntiCheatPacketProtectionPacket(snapshot));
                return;
            }

            if (op == "settings")
            {
                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, true, "settings",
                    "Anti-Cheat settings", BuildAntiCheatSettingsSummary(snapshot), "", BuildAntiCheatSettingsPacket(snapshot));
                return;
            }

            if (op == "settingssave")
            {
                if (request.Settings == null)
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, "settings",
                        "No Anti-Cheat settings were supplied.", new List<string>(), "");
                    return;
                }

                var incoming = request.Settings;
                var result = antiCheat.SaveGuiSettings(new AntiCheatGuiSettingsSaveRequest
                {
                    RequireClient = incoming.RequireClient,
                    RescanMinutes = incoming.RescanMinutes,
                    RuntimeRescanOnChange = incoming.RuntimeRescanOnChange,
                    RuntimeChangeRescanCooldownSeconds = incoming.RuntimeChangeRescanCooldownSeconds,
                    SimilarityDetectionEnabled = incoming.SimilarityDetectionEnabled,
                    SimilarityReviewThresholdPercent = incoming.SimilarityReviewThresholdPercent,
                    SimilarityHighThresholdPercent = incoming.SimilarityHighThresholdPercent
                });

                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, result.Success, "settings",
                    result.Message, BuildAntiCheatSettingsSummary(snapshot), "", BuildAntiCheatSettingsPacket(snapshot));
                return;
            }

            if (op == "blacklist")
            {
                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, true, "blacklist",
                    $"Client mod blacklist • {snapshot.ForbiddenModIds.Count} forbidden mod ID(s)",
                    BuildAntiCheatBlacklistLines(snapshot), "");
                return;
            }

            if (op is "blacklistadd" or "blacklistremove")
            {
                string action = op == "blacklistadd" ? "add" : "remove";
                var result = antiCheat.UpdateForbiddenMod(action, request.Search ?? "", action == "add" ? request.Secondary ?? "" : "");
                var snapshot = antiCheat.GetSnapshot();
                SendAntiCheatGuiResponse(player, request.RequestId, result.Success, "blacklist",
                    result.Message, BuildAntiCheatBlacklistLines(snapshot), "");
                return;
            }

            if (op == "blacklistresolve")
            {
                string modId = (request.Search ?? "").Trim().ToLowerInvariant();
                if (modId.Length == 0)
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, "blacklistresolve",
                        "Choose a blacklisted mod before resolving fingerprints.", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
                    return;
                }

                bool isBlacklisted = antiCheat.Config.ForbiddenModIds.Any(x =>
                    string.Equals(x?.Trim(), modId, StringComparison.OrdinalIgnoreCase));
                if (!isBlacklisted)
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, "blacklistresolve",
                        "Add the mod ID to the blacklist before resolving its Mod DB fingerprint.", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
                    return;
                }

                if (!fingerprintResolutionGate.Wait(0))
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, "blacklistresolve",
                        "Another Mod DB fingerprint resolution is already running. Wait for it to finish before starting the next one.",
                        BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
                    return;
                }

                SendAntiCheatGuiResponse(player, request.RequestId, true, "blacklistresolve",
                    $"Resolving official Mod DB fingerprints for {modId}...", BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
                _ = ResolveAntiCheatBlacklistFingerprintAsync(player, request.RequestId, modId);
                return;
            }

            if (op == "player")
            {
                string search = (request.Search ?? "").Trim();
                if (search.Length == 0)
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, op,
                        "Enter a player name or UID.", new List<string>(), search);
                    return;
                }

                var detail = antiCheat.GetPlayerSnapshot(search);
                if (detail == null)
                {
                    SendAntiCheatGuiResponse(player, request.RequestId, false, op,
                        "Player anti-cheat session was not found.", new List<string>(), search);
                    return;
                }

                var history = antiCheat.GetHistorySnapshot(detail.PlayerUid, 12);
                SendAntiCheatGuiResponse(player, request.RequestId, true, op,
                    $"{detail.PlayerName} • risk {detail.RiskScore}/100 • {RiskRating(detail.RiskScore)}", BuildAntiCheatPlayerLines(detail, history), detail.PlayerName);
                return;
            }

            SendAntiCheatGuiResponse(player, request.RequestId, false, op,
                $"Unknown anti-cheat GUI operation '{request.Operation}'.", new List<string>(), request.Search);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[AntiCheat] GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
            SendAntiCheatGuiResponse(player, request.RequestId, false, op,
                "Anti-cheat GUI request failed. Check the server log.", new List<string>(), request.Search);
        }
    }

    private async Task ResolveAntiCheatBlacklistFingerprintAsync(IServerPlayer player, long requestId, string modId)
    {
        AntiCheatModDbFingerprintResolution resolution;
        try
        {
            resolution = await AntiCheatModDbFingerprintResolver
                .ResolveAsync(modId, WebDashboardService.TargetGameVersion)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            resolution = new AntiCheatModDbFingerprintResolution
            {
                Success = false,
                ModId = modId,
                Message = $"Could not resolve Mod DB fingerprints: {ex.GetType().Name}: {ex.Message}"
            };
        }

        try
        {
            api.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    var antiCheat = module.AntiCheat;
                    if (antiCheat == null)
                    {
                        SendAntiCheatGuiResponse(player, requestId, false, "blacklistresolve",
                            "Anti-cheat service is no longer available.", new List<string>(), "");
                        return;
                    }

                    if (!resolution.Success)
                    {
                        SendAntiCheatGuiResponse(player, requestId, false, "blacklistresolve",
                            resolution.Message, BuildAntiCheatBlacklistLines(antiCheat.GetSnapshot()), "");
                        return;
                    }

                    var result = antiCheat.ApplyResolvedForbiddenFingerprint(resolution);
                    var snapshot = antiCheat.GetSnapshot();
                    string message = result.Success
                        ? $"{result.Message} Resolved {resolution.Releases.Count} release fingerprint{(resolution.Releases.Count == 1 ? "" : "s")}."
                        : result.Message;
                    SendAntiCheatGuiResponse(player, requestId, result.Success, "blacklistresolve",
                        message, BuildAntiCheatBlacklistLines(snapshot), "");
                }
                finally
                {
                    fingerprintResolutionGate.Release();
                }
            }, "anticheat-gui-fingerprint-resolve");
        }
        catch (Exception ex)
        {
            fingerprintResolutionGate.Release();
            api.Logger.Warning("[AntiCheat] Could not queue GUI fingerprint result for {0}: {1}: {2}", modId, ex.GetType().Name, ex.Message);
        }
    }

    private static List<string> BuildAntiCheatOverviewLines(BlockLogger.AntiCheat.AntiCheatWebSnapshot snapshot)
    {
        int highCritical = snapshot.Players.Count(x => x.RiskScore >= 50);
        int elevated = snapshot.Players.Count(x => x.RiskScore >= 25 && x.RiskScore < 50);
        int guarded = snapshot.Players.Count(x => x.RiskScore >= 10 && x.RiskScore < 25);
        return new List<string>
        {
            $"Enabled: {snapshot.Enabled}",
            $"Runtime available: {snapshot.Available}",
            $"Runtime status: {(string.IsNullOrWhiteSpace(snapshot.AvailabilityReason) ? "No detail available" : snapshot.AvailabilityReason)}",
            $"Network channel: {(string.IsNullOrWhiteSpace(snapshot.NetworkChannelName) ? "Unavailable" : snapshot.NetworkChannelName)}",
            $"Excess integrity packets dropped: {snapshot.NetworkPacketsDropped}",
            $"Server add-on installed: {snapshot.ServerCompanionInstalled}",
            $"Server add-on version: {(string.IsNullOrWhiteSpace(snapshot.ServerCompanionVersion) ? "not installed" : snapshot.ServerCompanionVersion)}",
            $"Expected companion version: {snapshot.ExpectedClientVersion}",
            $"Protocol: v{snapshot.ProtocolVersion}",
            $"Client required by policy: {snapshot.RequireClient}",
            $"Tracked online players: {snapshot.TrackedPlayers}",
            $"Integrity clients connected: {snapshot.ClientsConnected}/{snapshot.OnlinePlayers}",
            $"Clean: {snapshot.Clean}",
            $"Review: {snapshot.Review}",
            $"Suspicious: {snapshot.Suspicious}",
            $"High/Critical risk: {highCritical}",
            $"Elevated risk: {elevated}",
            $"Guarded risk: {guarded}",
            $"Awaiting/scanning: {snapshot.AwaitingOrScanning}",
            $"Missing/timed out: {snapshot.MissingOrTimedOut}",
            $"Blacklisted client mods: {snapshot.ForbiddenModIds.Count}",
            $"Persistent reports: {snapshot.History.TotalRecords} total / {snapshot.History.Last24Hours} last 24h",
            $"Players with stored reports: {snapshot.History.UniquePlayers}",
            $"Periodic rescan: every {snapshot.RescanMinutes} minute(s)",
            $"Runtime change rescan: {(snapshot.RuntimeRescanOnChange ? "Enabled" : "Disabled")}",
            $"Runtime rescan cooldown: {snapshot.RuntimeChangeRescanCooldownSeconds}s"
        };
    }

    private static List<string> BuildAntiCheatPlayerLines(
        BlockLogger.AntiCheat.AntiCheatPlayerWebSnapshot detail,
        BlockLogger.AntiCheat.AntiCheatHistoryWebSnapshot? history = null)
    {
        var lines = new List<string>
        {
            $"Player: {detail.PlayerName}",
            $"UID: {detail.PlayerUid}",
            $"Status: {detail.Status}",
            $"Risk score: {detail.RiskScore}/100",
            $"Risk rating: {RiskRating(detail.RiskScore)}",
            $"Client installed: {detail.ClientInstalled}",
            $"Client version: {detail.ClientVersion}",
            $"Game version: {detail.GameVersion}",
            $"Platform: {detail.Platform}",
            $"Excess integrity packets dropped: {detail.NetworkPacketsDropped}",
            $"Mods / assemblies: {detail.ModCount} / {detail.AssemblyCount}",
            $"Client DLL SHA-256: {ShortHash(detail.ClientAssemblySha256)}",
            $"Client package SHA-256: {ShortHash(detail.ClientPackageSha256)}",
            $"Client MVID: {detail.ClientAssemblyMvid}",
            $"Runtime generation: {detail.RuntimeGeneration}",
            $"Runtime assemblies: baseline {detail.RuntimeBaselineAssemblyCount}, loaded {detail.RuntimeLoadedAssemblyCount}, dynamic {detail.RuntimeDynamicAssemblyCount}",
            $"Post-baseline assemblies: {detail.RuntimeNewAssemblyCount} ({detail.RuntimeUnassociatedNewAssemblyCount} unassociated)",
            $"Runtime digest: {ShortHash(detail.RuntimeDigest)}",
            $"Findings: {detail.FindingCount}",
            $"Last scan: {FormatUtc(detail.LastScanUtc)}",
            $"Last heartbeat: {FormatUtc(detail.LastHeartbeatUtc)}",
            $"Last consequence: {(string.IsNullOrWhiteSpace(detail.LastConsequence) ? "None" : detail.LastConsequence)}"
        };

        foreach (var finding in detail.Findings.OrderByDescending(x => x.ConfidenceScore).ThenByDescending(x => x.Severity).Take(100))
            lines.Add($"FINDING\t{finding.Severity}\t{GuiField(finding.Confidence)}\t{finding.ConfidenceScore}\t{GuiField(finding.Category)}\t{GuiField(finding.Code)}\t{GuiField(finding.Summary)}\t{GuiField(finding.Subject)}\t{GuiField(finding.RecommendedAction)}");

        if (history != null)
        {
            lines.Add($"Recent stored reports shown: {history.Incidents.Count}");
            foreach (var incident in history.Incidents.Take(8))
                lines.Add(BuildAntiCheatReportLine(incident));
        }

        foreach (var mod in detail.Mods.Take(12))
            lines.Add($"MOD\t{GuiField(mod.ModId)}\t{GuiField(mod.Version)}\t{GuiField(mod.ModType)}\t{GuiField(mod.FileName)}");

        foreach (var runtime in detail.RuntimeNewAssemblies.Take(12))
            lines.Add($"Runtime + {runtime.AssemblyName} {runtime.Version} | {(runtime.IsDynamic ? "dynamic" : runtime.FileName)} | ctx {runtime.LoadContextName} | mod-associated {runtime.AssociatedWithLoadedMod}");

        foreach (var metric in detail.ScannerMetrics)
            lines.Add($"Scanner {metric.Scanner}: {(metric.Success ? "OK" : "FAILED")} | {metric.DurationMs}ms | mods {metric.ModCount} | asm {metric.AssemblyCount} | findings {metric.FindingCount} | cache {metric.CacheHits}/{metric.CacheMisses}");

        return lines;
    }

    private static List<string> BuildAntiCheatReportLines(BlockLogger.AntiCheat.AntiCheatHistoryWebSnapshot history)
    {
        var lines = new List<string>();
        foreach (var incident in history.Incidents) lines.Add(BuildAntiCheatReportLine(incident));
        if (lines.Count == 0) lines.Add(history.Enabled ? "No persistent Anti-Cheat reports match this filter." : "Persistent Anti-Cheat history is disabled.");
        return lines;
    }

    private static string BuildAntiCheatReportLine(BlockLogger.AntiCheat.AntiCheatIncidentWebSnapshot incident)
    {
        string time = FormatUtc(incident.UnixMilliseconds);
        return $"REPORT\t{GuiField(time)}\t{GuiField(incident.PlayerName)}\t{incident.RiskBefore}\t{incident.RiskAfter}\t{RiskRating(incident.RiskAfter)}\t{GuiField(incident.Confidence)}\t{incident.ConfidenceScore}\t{incident.Severity}\t{GuiField(incident.Category)}\t{GuiField(incident.Code)}\t{GuiField(incident.Summary)}\t{GuiField(incident.RecommendedAction)}";
    }

    private static List<AntiCheatConsequenceRuleGuiPacket> BuildAntiCheatConsequencePackets(
        IEnumerable<AntiCheatConsequenceRuleWebSnapshot>? rules)
    {
        return (rules ?? Enumerable.Empty<AntiCheatConsequenceRuleWebSnapshot>())
            .Select(rule => new AntiCheatConsequenceRuleGuiPacket
            {
                Id = rule.Id ?? "",
                Name = rule.Name ?? "",
                Enabled = rule.Enabled,
                TriggerType = rule.TriggerType ?? "FindingCode",
                TriggerValue = rule.TriggerValue ?? "",
                Subject = rule.Subject ?? "",
                Action = rule.Action ?? "Log",
                Duration = rule.Duration ?? "30m",
                Message = rule.Message ?? ""
            })
            .ToList();
    }

    private static AntiCheatConsequenceRule ToAntiCheatConsequenceRule(AntiCheatConsequenceRuleGuiPacket rule)
    {
        return new AntiCheatConsequenceRule
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id.Trim(),
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
        var list = (rules ?? Enumerable.Empty<AntiCheatConsequenceRuleGuiPacket>()).ToList();
        var lines = new List<string>();
        for (int i = 0; i < list.Count; i++)
        {
            var rule = list[i];
            lines.Add($"RULE\t{i}\t{GuiField(rule.Id)}\t{GuiField(rule.Name)}\t{(rule.Enabled ? 1 : 0)}\t{GuiField(rule.TriggerType)}\t{GuiField(rule.TriggerValue)}\t{GuiField(rule.Subject)}\t{GuiField(rule.Action)}\t{GuiField(rule.Duration)}\t{GuiField(rule.Message)}");
        }
        if (lines.Count == 0) lines.Add("No automatic enforcement rules are configured. Detection and reporting still continue normally.");
        return lines;
    }

    private static List<string> BuildAntiCheatBlacklistLines(BlockLogger.AntiCheat.AntiCheatWebSnapshot snapshot)
    {
        var fingerprints = snapshot.ForbiddenFingerprints
            .Where(x => !string.IsNullOrWhiteSpace(x.ModId))
            .GroupBy(x => x.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (string id in snapshot.ForbiddenModIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            fingerprints.TryGetValue(id, out var fp);
            string source = fp?.Source ?? "ID policy";
            int hashes = (fp?.AssemblyHashCount ?? 0) + (fp?.AssemblyStructureHashCount ?? 0) + (fp?.AssemblyBehaviorHashCount ?? 0);
            string package = fp?.HasPackageHash == true ? "package" : "no-package";
            string content = fp?.HasContentHash == true ? "content" : "no-content";
            lines.Add($"BLACKLIST\t{GuiField(id)}\t{GuiField(source)}\t{package}\t{content}\t{hashes}\t{fp?.ManualEntryCount ?? 0}\t{fp?.ResolvedVersions?.Count ?? 0}\t{GuiField(fp?.ResolvedVersion)}\t{GuiField(FormatUtc(fp?.ResolvedUtc ?? 0))}");
        }
        if (lines.Count == 0) lines.Add("No client mod IDs are currently blacklisted.");
        return lines;
    }

    private static string GuiField(string? value) => (value ?? "")
        .Replace('\t', ' ')
        .Replace('\r', ' ')
        .Replace('\n', ' ');

    private static string RiskRating(int risk)
    {
        risk = Math.Clamp(risk, 0, 100);
        if (risk >= 75) return "Critical";
        if (risk >= 50) return "High";
        if (risk >= 25) return "Elevated";
        if (risk >= 10) return "Guarded";
        return "Low";
    }

    private static AntiCheatPacketProtectionGuiPacket BuildAntiCheatPacketProtectionPacket(AntiCheatWebSnapshot snapshot)
    {
        var pg = snapshot.PacketGuard ?? new AntiCheatPacketGuardWebSnapshot();
        var history = snapshot.History ?? new AntiCheatHistorySummaryWebSnapshot();
        return new AntiCheatPacketProtectionGuiPacket
        {
            PacketGuardEnabled = pg.Enabled,
            IngressEnabled = pg.IngressEnabled,
            IngressActive = pg.IngressActive,
            IngressStatus = pg.IngressStatus,
            PlayerWarningEnabled = pg.PlayerWarningEnabled,
            PlayerWarningCooldownSeconds = pg.PlayerWarningCooldownSeconds,
            AccelerationSampleMinMs = pg.AccelerationSampleMinMs,
            AccelerationSmoothingFactor = pg.AccelerationSmoothingFactor,
            NoclipEnabled = pg.NoclipEnabled,
            NoclipRequiredCollisionSamples = pg.NoclipRequiredCollisionSamples,
            NoclipMinPenetrationDepth = pg.NoclipMinPenetrationDepth,
            NoclipEndpointMinNetMovement = pg.NoclipEndpointMinNetMovement,
            MountAccelerationMinSpeedRatio = pg.MountAccelerationMinSpeedRatio,
            DiagnosticContextEnabled = pg.DiagnosticContextEnabled,
            DiagnosticSuppressionEnabled = pg.DiagnosticSuppressionEnabled,
            DiagnosticSpeedHistorySamples = pg.DiagnosticSpeedHistorySamples,

            MovementEnabled = pg.MovementEnabled,
            MovementSampleMs = pg.MovementSampleMs,
            HardMoveDistance = pg.HardMoveDistance,
            MovementConfirmSamples = pg.MovementConfirmSamples,
            MovementConfirmWindowMs = pg.MovementConfirmWindowMs,
            MovementExtremeMultiplier = pg.MovementExtremeMultiplier,
            CorrectionGraceMs = pg.CorrectionGraceMs,
            RejectUnauthorizedMovement = pg.RejectUnauthorizedMovement,
            ExemptAdmins = pg.ExemptAdmins,
            ExemptMountedPlayers = pg.ExemptMountedPlayers,
            MountGraceMs = pg.MountGraceMs,
            EngineTeleportGraceMs = pg.EngineTeleportGraceMs,
            TPRunesCompatibility = pg.TPRunesCompatibility,
            TPRunesGraceMs = pg.TPRunesGraceMs,
            TPRunesDetected = pg.TPRunesDetected,
            Th3EssentialsSpawnCompatibility = pg.Th3EssentialsSpawnCompatibility,
            Th3EssentialsSpawnGraceMs = pg.Th3EssentialsSpawnGraceMs,
            Th3EssentialsDetected = pg.Th3EssentialsDetected,

            ReachEnabled = pg.ReachEnabled,
            ReachTolerance = pg.ReachTolerance,
            ReachLatencyCompensation = pg.ReachLatencyCompensation,
            ReachMaxLatencyCompensation = pg.ReachMaxLatencyCompensation,
            ReachPositionHistoryEnabled = pg.ReachPositionHistoryEnabled,
            ReachPositionHistoryMs = pg.ReachPositionHistoryMs,
            ReachMaxRewindMs = pg.ReachMaxRewindMs,
            ReachUseExtraTolerance = pg.ReachUseExtraTolerance,
            ReachBreakExtraTolerance = pg.ReachBreakExtraTolerance,
            ReachNearThresholdStrikes = pg.ReachNearThresholdStrikes,
            ReachStrikeWindowMs = pg.ReachStrikeWindowMs,
            ReachStrikeDecayMs = pg.ReachStrikeDecayMs,
            ReachHardExcess = pg.ReachHardExcess,
            RejectOutOfReachActions = pg.RejectOutOfReachActions,

            MiningEnabled = pg.MiningEnabled,
            VeinWindowMs = pg.VeinWindowMs,
            ConnectedBreakLimit = pg.ConnectedBreakLimit,
            MiningDuplicateTargetMs = pg.MiningDuplicateTargetMs,
            RejectConnectedBreakBursts = pg.RejectConnectedBreakBursts,
            MiningCadenceValidationEnabled = pg.MiningCadenceValidationEnabled,
            MiningCadenceMinExpectedMs = pg.MiningCadenceMinExpectedMs,
            MiningCadenceSuspiciousRatio = pg.MiningCadenceSuspiciousRatio,
            MiningCadenceConfirmations = pg.MiningCadenceConfirmations,
            MiningCadenceConfirmationWindowMs = pg.MiningCadenceConfirmationWindowMs,
            RejectImpossibleMiningCadence = pg.RejectImpossibleMiningCadence,

            TpsAwarenessEnabled = pg.TpsAwarenessEnabled,
            TpsDegradedPercent = pg.TpsDegradedPercent,
            TpsCriticalPercent = pg.TpsCriticalPercent,
            TpsMaxTimingMultiplier = pg.TpsMaxTimingMultiplier,
            TpsSuppressTimingRejectsWhenCritical = pg.TpsSuppressTimingRejectsWhenCritical,
            TpsReady = pg.TpsReady,
            TpsTarget = pg.TpsTarget,
            TpsEstimated = pg.TpsEstimated,
            TpsHealthPercent = pg.TpsHealthPercent,
            TpsBand = pg.TpsBand ?? "Disabled",

            PerFamilyRiskEnabled = pg.PerFamilyRiskEnabled,
            MovementRiskDecaySeconds = pg.MovementRiskDecaySeconds,
            MovementRiskReviewThreshold = pg.MovementRiskReviewThreshold,
            MovementRiskSuspiciousThreshold = pg.MovementRiskSuspiciousThreshold,
            ReachRiskDecaySeconds = pg.ReachRiskDecaySeconds,
            ReachRiskReviewThreshold = pg.ReachRiskReviewThreshold,
            ReachRiskSuspiciousThreshold = pg.ReachRiskSuspiciousThreshold,
            MiningRiskDecaySeconds = pg.MiningRiskDecaySeconds,
            MiningRiskReviewThreshold = pg.MiningRiskReviewThreshold,
            MiningRiskSuspiciousThreshold = pg.MiningRiskSuspiciousThreshold,
            PacketAbuseRiskDecaySeconds = pg.PacketAbuseRiskDecaySeconds,
            PacketAbuseRiskReviewThreshold = pg.PacketAbuseRiskReviewThreshold,
            PacketAbuseRiskSuspiciousThreshold = pg.PacketAbuseRiskSuspiciousThreshold,
            ClientIntegrityRiskDecaySeconds = pg.ClientIntegrityRiskDecaySeconds,
            ClientIntegrityRiskReviewThreshold = pg.ClientIntegrityRiskReviewThreshold,
            ClientIntegrityRiskSuspiciousThreshold = pg.ClientIntegrityRiskSuspiciousThreshold,

            IncidentCorrelationEnabled = pg.IncidentCorrelationEnabled,
            IncidentCorrelationWindowSeconds = pg.IncidentCorrelationWindowSeconds,
            IncidentCorrelationRequiredCategories = pg.IncidentCorrelationRequiredCategories,
            IncidentCorrelationMaxRiskBonus = pg.IncidentCorrelationMaxRiskBonus,
            HistoryEnabled = history.Enabled,
            HistoryRetentionDays = history.RetentionDays,
            HistoryMaxRecords = history.MaxRecords
        };
    }

    private static List<string> BuildAntiCheatPacketProtectionSummary(AntiCheatWebSnapshot snapshot)
    {
        var pg = snapshot.PacketGuard ?? new AntiCheatPacketGuardWebSnapshot();
        return new List<string>
        {
            $"PacketGuard: {(pg.Enabled ? "on" : "off")} • chat warnings {(pg.PlayerWarningEnabled ? "on" : "off")} • noclip {(pg.NoclipEnabled ? "on" : "off")}",
            $"Movement {(pg.MovementEnabled ? "on" : "off")} • reach {(pg.ReachEnabled ? "on" : "off")} • mining {(pg.MiningEnabled ? "on" : "off")} • TPS {(pg.TpsAwarenessEnabled ? "aware" : "off")}",
            $"Acceleration sample {pg.AccelerationSampleMinMs} ms • smoothing {pg.AccelerationSmoothingFactor:0.##} • mount ratio {pg.MountAccelerationMinSpeedRatio:0.##}",
            $"Per-family risk {(pg.PerFamilyRiskEnabled ? "on" : "off")} • diagnostics context {(pg.DiagnosticContextEnabled ? "on" : "off")} • suppressions {(pg.DiagnosticSuppressionEnabled ? "on" : "off")}"
        };
    }

    private static AntiCheatGuiSettingsPacket BuildAntiCheatSettingsPacket(AntiCheatWebSnapshot snapshot)
    {
        var pg = snapshot.PacketGuard ?? new AntiCheatPacketGuardWebSnapshot();
        var history = snapshot.History ?? new AntiCheatHistorySummaryWebSnapshot();
        return new AntiCheatGuiSettingsPacket
        {
            RequireClient = snapshot.RequireClient,
            RescanMinutes = snapshot.RescanMinutes,
            RuntimeRescanOnChange = snapshot.RuntimeRescanOnChange,
            RuntimeChangeRescanCooldownSeconds = snapshot.RuntimeChangeRescanCooldownSeconds,
            SimilarityDetectionEnabled = snapshot.SimilarityDetectionEnabled,
            SimilarityReviewThresholdPercent = snapshot.SimilarityReviewThresholdPercent,
            SimilarityHighThresholdPercent = snapshot.SimilarityHighThresholdPercent,
            PacketGuardEnabled = pg.Enabled,
            MovementEnabled = pg.MovementEnabled,
            HardMoveDistance = pg.HardMoveDistance,
            RejectUnauthorizedMovement = pg.RejectUnauthorizedMovement,
            Th3EssentialsSpawnCompatibility = pg.Th3EssentialsSpawnCompatibility,
            Th3EssentialsSpawnGraceMs = pg.Th3EssentialsSpawnGraceMs,
            Th3EssentialsDetected = pg.Th3EssentialsDetected,
            ReachEnabled = pg.ReachEnabled,
            ReachTolerance = pg.ReachTolerance,
            ReachHardExcess = pg.ReachHardExcess,
            RejectOutOfReachActions = pg.RejectOutOfReachActions,
            MiningEnabled = pg.MiningEnabled,
            MiningCadenceValidationEnabled = pg.MiningCadenceValidationEnabled,
            MiningCadenceSuspiciousRatio = pg.MiningCadenceSuspiciousRatio,
            MiningCadenceConfirmations = pg.MiningCadenceConfirmations,
            RejectConnectedBreakBursts = pg.RejectConnectedBreakBursts,
            RejectImpossibleMiningCadence = pg.RejectImpossibleMiningCadence,
            TpsAwarenessEnabled = pg.TpsAwarenessEnabled,
            TpsDegradedPercent = pg.TpsDegradedPercent,
            TpsCriticalPercent = pg.TpsCriticalPercent,
            TpsMaxTimingMultiplier = pg.TpsMaxTimingMultiplier,
            TpsSuppressTimingRejectsWhenCritical = pg.TpsSuppressTimingRejectsWhenCritical,
            IncidentCorrelationEnabled = pg.IncidentCorrelationEnabled,
            IncidentCorrelationWindowSeconds = pg.IncidentCorrelationWindowSeconds,
            IncidentCorrelationRequiredCategories = pg.IncidentCorrelationRequiredCategories,
            IncidentCorrelationMaxRiskBonus = pg.IncidentCorrelationMaxRiskBonus,
            HistoryEnabled = history.Enabled,
            HistoryRetentionDays = history.RetentionDays,
            HistoryMaxRecords = history.MaxRecords
        };
    }

    private static List<string> BuildAntiCheatSettingsSummary(AntiCheatWebSnapshot snapshot)
    {
        return new List<string>
        {
            $"Client requirement: {(snapshot.RequireClient ? "required" : "optional")}",
            $"Client rescan: every {snapshot.RescanMinutes} minute(s) • runtime-change rescan {(snapshot.RuntimeRescanOnChange ? "on" : "off")}",
            $"Similarity detection: {(snapshot.SimilarityDetectionEnabled ? "on" : "off")} • review {snapshot.SimilarityReviewThresholdPercent}% • high {snapshot.SimilarityHighThresholdPercent}%",
            "PacketGuard settings are managed under Anti-Cheat → Packet Protection."
        };
    }

    private static string ShortHash(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0) return "n/a";
        return text.Length <= 16 ? text : text[..16] + "...";
    }

    private static string FormatUtc(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0) return "never";
        try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); }
        catch { return unixMilliseconds.ToString(CultureInfo.InvariantCulture); }
    }

    private void SendAntiCheatGuiResponse(
        IServerPlayer player,
        long requestId,
        bool success,
        string operation,
        string message,
        List<string> lines,
        string? selectedPlayer,
        AntiCheatGuiSettingsPacket? settings = null,
        List<AntiCheatConsequenceRuleGuiPacket>? consequenceRules = null,
        AntiCheatPacketProtectionGuiPacket? packetSettings = null)
    {
        channel?.SendPacket(new AntiCheatGuiResponse
        {
            RequestId = requestId,
            Success = success,
            Operation = operation ?? "overview",
            Message = message ?? "",
            Lines = lines ?? new List<string>(),
            SelectedPlayer = selectedPlayer ?? "",
            Enabled = module.AntiCheat?.Config.Enabled ?? false,
            Settings = settings,
            ConsequenceRules = consequenceRules ?? new List<AntiCheatConsequenceRuleGuiPacket>(),
            PacketSettings = packetSettings
        }, player);
    }

    private void HandleWebDashboard(IServerPlayer player, WebDashboardRequest request)
    {
        if (channel == null) return;

        string op = (request.Operation ?? "status").Trim().ToLowerInvariant();
        if (op == "panel") op = "status";
        bool modifying = op is "setbool" or "setint" or "settext" or "mapsave";

        if (modifying)
        {
            if (!player.HasPrivilege("controlserver"))
            {
                SendWebDashboardResponse(
                    player,
                    request.RequestId,
                    false,
                    "You need the controlserver privilege to change Web Server settings.");
                return;
            }
        }
        else if (!player.HasPrivilege(InvestigatorPrivilege))
        {
            SendWebDashboardResponse(
                player,
                request.RequestId,
                false,
                "You do not have the blocklogger privilege.");
            return;
        }

        if (op is "map" or "mapsave")
        {
            HandleWebMapGuiRequest(player, request, op);
            return;
        }

        var web = module.WebDashboard;
        if (web == null)
        {
            SendWebDashboardResponse(
                player,
                request.RequestId,
                false,
                "Web Dashboard service is not available.");
            return;
        }

        try
        {
            string message = "Web Server status";
            bool success = true;
            string key = (request.SettingKey ?? "").Trim().ToLowerInvariant();

            if (op == "setbool")
            {
                message = key switch
                {
                    "enabled" => web.SetEnabled(request.BoolValue),
                    "securecookies" => web.SetSecureCookies(request.BoolValue),
                    _ => $"Unknown Web Server setting '{request.SettingKey}'."
                };

                success = key is "enabled" or "securecookies";
            }
            else if (op == "setint")
            {
                message = key switch
                {
                    "port" => web.SetPort(request.IntValue),
                    "sessiontimeoutminutes" => web.SetSessionTimeout(request.IntValue),
                    _ => $"Unknown Web Server setting '{request.SettingKey}'."
                };

                success = key is "port" or "sessiontimeoutminutes";
            }
            else if (op == "settext")
            {
                message = key switch
                {
                    "bindaddress" => web.SetBindAddress(request.StringValue),
                    "publicurl" => web.SetPublicUrl(request.StringValue),
                    "adminusername" => web.SetUsername(request.StringValue),
                    _ => $"Unknown Web Server setting '{request.SettingKey}'."
                };

                success = key is "bindaddress" or "publicurl" or "adminusername";
            }
            else if (op is not ("status" or "settings"))
            {
                SendWebDashboardResponse(
                    player,
                    request.RequestId,
                    false,
                    $"Unknown Web Server operation '{request.Operation}'.");
                return;
            }

            SendWebDashboardResponse(
                player,
                request.RequestId,
                success,
                message);
        }
        catch (Exception ex)
        {
            api.Logger.Warning(
                "[WebDashboard] GUI request failed: {0}: {1}",
                ex.GetType().Name,
                ex.Message);

            SendWebDashboardResponse(
                player,
                request.RequestId,
                false,
                "Web Server request failed. Check the server log.");
        }
    }

    private void HandleWebMapGuiRequest(IServerPlayer player, WebDashboardRequest request, string op)
    {
        var map = module.WebMap;
        if (map == null)
        {
            SendWebDashboardResponse(player, request.RequestId, false,
                "Web Map feature is not available in this running server configuration.");
            return;
        }

        try
        {
            if (op == "mapsave")
            {
                var posted = request.WebMapSettings;
                if (posted == null)
                {
                    SendWebDashboardResponse(player, request.RequestId, false, "No Web Map settings were supplied.");
                    return;
                }

                WebMapConfig config = map.Config;
                config.Enabled = posted.Enabled;
                config.BindAddress = posted.BindAddress ?? "0.0.0.0";
                config.Port = posted.Port;
                config.Title = posted.Title ?? "Newfie's Server Map";
                config.PublicUrl = posted.PublicUrl ?? "";
                config.DashboardUrl = posted.DashboardUrl ?? "";
                // A single explicit GUI switch controls the privacy gate and public player layer.
                config.AllowLivePlayerData = posted.ShowPlayers;
                config.ShowPlayers = posted.ShowPlayers;
                config.ShowWaypoints = posted.ShowWaypoints;
                config.ShowActivity = posted.ShowActivity;
                config.ShowBadBlockEntities = posted.ShowBadBlockEntities;
                config.PlayerMajorWaypointLimit = posted.PlayerMajorWaypointLimit;
                config.PlayerMinorWaypointLimit = posted.PlayerMinorWaypointLimit;
                config.MaximumZoomOutSpanBlocks = posted.MaximumZoomOutSpanBlocks;
                config.DefaultViewSpanBlocks = posted.DefaultViewSpanBlocks;
                config.TerrainTileSize = posted.TerrainTileSize;
                config.ActivityWindowMinutes = posted.ActivityWindowMinutes;
                config.MaxActivityMarkers = posted.MaxActivityMarkers;
                config.ExportTerrainOnStart = posted.ExportTerrainOnStart;
                config.AutomaticStartupExportEnabled = posted.AutomaticStartupExportEnabled;
                config.AutomaticStartupExportIntervalDays = posted.AutomaticStartupExportIntervalDays;
                config.UseIncrementalTerrainExport = posted.UseIncrementalTerrainExport;
                config.LockServerDuringTerrainExport = posted.LockServerDuringTerrainExport;

                map.SaveConfig(config);
                SendWebDashboardResponse(player, request.RequestId, true, "Web Map settings saved.");
                return;
            }

            SendWebDashboardResponse(player, request.RequestId, true, "Web Map configuration");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[WebMap] Shift+L GUI request failed: {0}: {1}", ex.GetType().Name, ex.Message);
            SendWebDashboardResponse(player, request.RequestId, false, "Web Map request failed. Check the server log.");
        }
    }

    private WebMapGuiSettingsPacket? BuildWebMapGuiSettings()
    {
        var map = module.WebMap;
        if (map == null) return null;

        WebMapConfig config = map.Config;
        return new WebMapGuiSettingsPacket
        {
            Enabled = config.Enabled,
            Running = map.IsRunning,
            BindAddress = config.BindAddress,
            Port = config.Port,
            Title = config.Title,
            PublicUrl = config.PublicUrl,
            DashboardUrl = config.DashboardUrl,
            ShowPlayers = config.AllowLivePlayerData && config.ShowPlayers,
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
            TerrainExportInProgress = map.IsTerrainExportInProgress,
            LastError = map.LastError ?? ""
        };
    }

    private void SendWebDashboardResponse(
        IServerPlayer player,
        long requestId,
        bool success,
        string message)
    {
        if (channel == null) return;

        var web = module.WebDashboard;
        var config = web?.Config;
        channel.SendPacket(new WebDashboardResponse
        {
            RequestId = requestId,
            Success = success,
            Message = message,
            Lines = web?.GetGuiStatusLines() ?? new List<string>(),
            Enabled = config?.Enabled ?? false,
            Running = web?.IsRunning ?? false,
            BindAddress = config?.BindAddress ?? "",
            Port = config?.Port ?? 0,
            AdminUsername = config?.AdminUsername ?? "",
            PasswordConfigured = web?.PasswordConfigured ?? false,
            ActiveSessions = web?.ActiveSessionCount ?? 0,
            SessionTimeoutMinutes = config?.SessionTimeoutMinutes ?? 0,
            SecureCookies = config?.SecureCookies ?? false,
            LastError = web?.LastError ?? "",
            LocalAnonymousMode = web?.LocalAnonymousMode ?? false,
            LocalUrl = web?.LocalDashboardUrl ?? "",
            PublicUrl = web?.PublicDashboardUrl ?? "",
            WebMapSettings = BuildWebMapGuiSettings()
        }, player);
    }

    private void HandleLoginProtection(IServerPlayer player, LoginProtectionRequest request)
    {
        if (channel == null) return;

        string op = (request.Operation ?? "status").Trim().ToLowerInvariant();
        bool modifying = op is "setbool" or "setint";

        if (modifying)
        {
            if (!player.HasPrivilege("controlserver"))
            {
                SendLoginProtectionResponse(
                    player,
                    request.RequestId,
                    false,
                    "You need the controlserver privilege to change Login Protection settings.",
                    op);
                return;
            }
        }
        else if (!player.HasPrivilege(InvestigatorPrivilege))
        {
            SendLoginProtectionResponse(
                player,
                request.RequestId,
                false,
                "You do not have the blocklogger privilege.",
                op);
            return;
        }

        var loginProtection = LoginProtectionSystem.Current;
        if (loginProtection == null)
        {
            SendLoginProtectionResponse(
                player,
                request.RequestId,
                false,
                "Login Protection is not available.",
                op);
            return;
        }

        try
        {
            string message;

            if (op == "setbool")
            {
                message = loginProtection.SetBooleanSettingFromGui(
                    request.SettingKey,
                    request.BoolValue);
            }
            else if (op == "setint")
            {
                message = loginProtection.SetIntegerSettingFromGui(
                    request.SettingKey,
                    request.IntValue);
            }
            else if (op == "settings")
            {
                message = "Login Protection settings";
            }
            else if (op == "status")
            {
                message = "Login Protection status";
            }
            else
            {
                SendLoginProtectionResponse(
                    player,
                    request.RequestId,
                    false,
                    $"Unknown Login Protection operation '{request.Operation}'.",
                    op);
                return;
            }

            SendLoginProtectionResponse(
                player,
                request.RequestId,
                true,
                message,
                op);
        }
        catch (Exception ex)
        {
            api.Logger.Warning(
                "[LoginProtection] GUI request failed: {0}: {1}",
                ex.GetType().Name,
                ex.Message);

            SendLoginProtectionResponse(
                player,
                request.RequestId,
                false,
                "Login Protection request failed. Check the server log.",
                op);
        }
    }

    private void SendLoginProtectionResponse(
        IServerPlayer player,
        long requestId,
        bool success,
        string message,
        string operation)
    {
        if (channel == null) return;

        var loginProtection = LoginProtectionSystem.Current;
        if (loginProtection == null)
        {
            channel.SendPacket(new LoginProtectionResponse
            {
                RequestId = requestId,
                Success = false,
                Message = "Login Protection is not available."
            }, player);
            return;
        }

        var config = loginProtection.Config;
        bool settingsView =
            operation is "settings" or "setbool" or "setint";

        channel.SendPacket(new LoginProtectionResponse
        {
            RequestId = requestId,
            Success = success,
            Message = message,
            Lines = settingsView
                ? loginProtection.GetGuiSettingsLines()
                : loginProtection.GetGuiStatusLines(),
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
            ActiveProtectedCount = loginProtection.ActiveProtectedCount,
            RecoverSwimmingOnLogin = config.RecoverSwimmingOnLogin
        }, player);
    }

    private void HandleProfiler(IServerPlayer player, ProfilerRequest request)
    {
        if (channel == null) return;

        string op = (request.Operation ?? "get").Trim().ToLowerInvariant();

        if (op is "start" or "setenabled")
        {
            if (!player.HasPrivilege("controlserver"))
            {
                SendProfilerResponse(player, request.RequestId, false,
                    "You need the controlserver privilege to start an engine profile.");
                return;
            }
        }
        else if (!player.HasPrivilege(InvestigatorPrivilege))
        {
            SendProfilerResponse(player, request.RequestId, false,
                "You do not have the blocklogger privilege.");
            return;
        }

        var health = module.ServerHealth;
        if (health == null)
        {
            SendProfilerResponse(player, request.RequestId, false,
                "Server diagnostics are not available.");
            return;
        }

        // Live Shift+L refreshes need only the current counters/status. Sending the
        // full 30-minute history every two seconds created unnecessary server GC,
        // network serialization and client GUI work.
        if (op == "get" && request.Compact)
        {
            SendProfilerCompactResponse(player, request.RequestId);
            return;
        }

        try
        {
            string message = "Server profiler & diagnostics";
            bool success = true;

            if (op == "setenabled")
            {
                module.Config.ProfilerEnabled = request.BoolValue;
                module.SaveRuntimeConfig();
                message = request.BoolValue
                    ? "Profiler enabled. Rolling diagnostics are now collecting."
                    : "Profiler disabled. Rolling diagnostics and any active engine profile were stopped.";
            }
            else if (op == "start")
            {
                success = health.StartManualProfile(request.DurationSeconds, out message);
            }
            else if (op != "get")
            {
                SendProfilerResponse(player, request.RequestId, false,
                    $"Unknown profiler operation '{request.Operation}'.");
                return;
            }

            SendProfilerResponse(player, request.RequestId, success, message);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[ServerHealth] GUI profiler request failed: {0}: {1}", ex.GetType().Name, ex.Message);
            SendProfilerResponse(player, request.RequestId, false,
                "Profiler request failed. Check the server log.");
        }
    }

    private void SendProfilerCompactResponse(IServerPlayer player, long requestId)
    {
        if (channel == null) return;

        var health = module.ServerHealth;
        if (health == null)
        {
            channel.SendPacket(new ProfilerResponse
            {
                RequestId = requestId,
                Success = false,
                Compact = true,
                Message = "Server diagnostics are not available."
            }, player);
            return;
        }

        var live = health.GetLiveState();
        channel.SendPacket(new ProfilerResponse
        {
            RequestId = requestId,
            Success = true,
            Compact = true,
            Enabled = live.Enabled,
            ProfileRunning = live.ProfileRunning,
            ProfileDurationSeconds = live.ProfileDurationSeconds,
            ProfileRemainingSeconds = live.ProfileRemainingSeconds,
            Current = ToProfilerSample(live.Current)
        }, player);
    }

    private void SendProfilerResponse(
        IServerPlayer player,
        long requestId,
        bool success,
        string message)
    {
        if (channel == null) return;

        var health = module.ServerHealth;
        if (health == null)
        {
            channel.SendPacket(new ProfilerResponse
            {
                RequestId = requestId,
                Success = false,
                Message = "Server diagnostics are not available."
            }, player);
            return;
        }

        var snapshot = health.GetSnapshot();

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
            SlowSpikes = snapshot.SlowSpikes.Select(x => new ProfilerSlowSpikePacket
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
            Hotspots = snapshot.Hotspots.Select(x => new ProfilerHotspotPacket
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
            ModHotspots = snapshot.ModHotspots.Select(x => new ProfilerModHotspotPacket
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
            Phases = snapshot.Phases.Select(x => new ProfilerPhasePacket
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
            ProfileSlowTicks = snapshot.ProfileSlowTicks.Select(x => new ProfilerProfileSlowTickPacket
            {
                UnixMilliseconds = x.UnixMilliseconds,
                TickMs = x.TickMs,
                TopCauseSummary = x.TopCauseSummary
            }).ToList(),
            OffThreadReports = snapshot.OffThreadReports.Select(x => new ProfilerOffThreadReportPacket
            {
                DurationMs = x.DurationMs,
                Summary = x.Summary,
                ModId = x.ModId,
                Confidence = x.Confidence
            }).ToList(),
            DiagnosticIncidents = snapshot.DiagnosticIncidents.Select(x => new ProfilerDiagnosticIncidentPacket
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
            LocalWebUrl = module.WebDashboard?.LocalDashboardUrl ?? "",
            PublicWebUrl = module.WebDashboard?.PublicDashboardUrl ?? ""
        }, player);
    }

    private static ProfilerSamplePacket ToProfilerSample(ServerHealthSample sample) =>
        new()
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

    private void HandleStatus(IServerPlayer player, InvestigatorStatusRequest request)
    {
        if (channel == null) return;
        if (!player.HasPrivilege(InvestigatorPrivilege))
        {
            channel.SendPacket(new InvestigatorStatusResponse
            {
                RequestId = request.RequestId,
                Error = "You do not have the blocklogger privilege."
            }, player);
            return;
        }

        var storage = module.Storage;
        if (storage == null)
        {
            channel.SendPacket(new InvestigatorStatusResponse
            {
                RequestId = request.RequestId,
                Error = "BlockLogger storage is not available."
            }, player);
            return;
        }

        var config = module.Config;
        var lines = new List<string>
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
        if (channel == null) return;
        if (!player.HasPrivilege(InvestigatorPrivilege))
        {
            SendError(player, request, "You do not have the blocklogger privilege.");
            return;
        }

        var queries = module.Queries;
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

        try
        {
            var view = NormalizeView(request.View);
            var config = module.Config;
            var radius = Math.Clamp(request.Radius, 0, config.MaxRadius);
            // MaxDisplayCount in NewfiesBlocklogger.json is the server-authoritative
            // query cap. It defaults to 100 and can be configured up to the absolute
            // config safety ceiling.
            var limit = Math.Clamp(request.Limit, 1, config.MaxDisplayCount);
            var search = TrimFilter(request.Search, 80);
            var action = NormalizeAction(view, request.Action);

            if (view == "players")
            {
                if (string.IsNullOrWhiteSpace(search))
                {
                    activeQueries.TryRemove(player.PlayerUID, out _);
                    SendError(player, request, "Enter a player name or UID.");
                    return;
                }

                _ = QueryPlayerAsync(player, request.RequestId, search, limit, action, queries);
                return;
            }

            if (!TryResolveTarget(player, request.UseSelection, view, out var target))
            {
                activeQueries.TryRemove(player.PlayerUID, out _);
                SendError(player, request, request.UseSelection
                    ? "Look at a block or entity first, or use Current position."
                    : "Unable to resolve your current position.");
                return;
            }

            _ = QueryAsync(player, request.RequestId, view, target, radius, limit, search, action, queries);
        }
        catch (Exception ex)
        {
            activeQueries.TryRemove(player.PlayerUID, out _);
            api.Logger.Warning("[BlockLogger] Investigator request rejected: {0}: {1}", ex.GetType().Name, ex.Message);
            SendError(player, request, "Invalid search request.");
        }
    }

    private async Task QueryPlayerAsync(
        IServerPlayer player,
        long requestId,
        string playerFilter,
        int limit,
        string? category,
        LogQueryService queries)
    {
        try
        {
            var languageCode = player.LanguageCode;
            var rows = await queries.QueryPlayerAsync(playerFilter, limit, category).ConfigureAwait(false);
            var lines = new List<string>(rows.Count);
            foreach (var row in rows)
                lines.Add(FormatPlayerLine(row, languageCode));

            var response = new InvestigatorQueryResponse
            {
                RequestId = requestId,
                Success = true,
                View = "players",
                Header = $"Player history — {playerFilter} — {rows.Count} result(s)",
                Lines = lines
            };

            api.Event.EnqueueMainThreadTask(() => channel?.SendPacket(response, player), "blocklogger-player-result");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger] Player history query failed: {0}: {1}", ex.GetType().Name, ex.Message);
            var response = new InvestigatorQueryResponse
            {
                RequestId = requestId,
                View = "players",
                Error = "Player history search failed. Check the server log."
            };
            api.Event.EnqueueMainThreadTask(() => channel?.SendPacket(response, player), "blocklogger-player-error");
        }
        finally
        {
            activeQueries.TryRemove(player.PlayerUID, out _);
        }
    }

    private async Task QueryAsync(
        IServerPlayer player,
        long requestId,
        string view,
        BlockPos target,
        int radius,
        int limit,
        string? search,
        string? action,
        LogQueryService queries)
    {
        try
        {
            var languageCode = player.LanguageCode;
            List<LogEventRecord> rows = view == "entities"
                ? await queries.QueryEntitiesAsync(target.X, target.Y, target.Z, target.dimension, radius, limit, search, action).ConfigureAwait(false)
                : await queries.QueryBlocksAsync(target.X, target.Y, target.Z, target.dimension, radius, limit, search, action).ConfigureAwait(false);

            var lines = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                lines.Add(FormatLine(row, target, languageCode));
            }

            var response = new InvestigatorQueryResponse
            {
                RequestId = requestId,
                Success = true,
                View = view,
                Header = $"{Title(view)} — {rows.Count} result(s), radius {radius}, target {target.X}, {target.Y}, {target.Z}",
                Lines = lines
            };

            api.Event.EnqueueMainThreadTask(() => channel?.SendPacket(response, player), "blocklogger-investigator-result");
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[BlockLogger] Investigator query failed: {0}: {1}", ex.GetType().Name, ex.Message);
            var response = new InvestigatorQueryResponse
            {
                RequestId = requestId,
                View = view,
                Error = "Search failed. Check the server log."
            };
            api.Event.EnqueueMainThreadTask(() => channel?.SendPacket(response, player), "blocklogger-investigator-error");
        }
        finally
        {
            activeQueries.TryRemove(player.PlayerUID, out _);
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

        if (view == "entities" && player.CurrentEntitySelection?.Entity != null &&
            EntityPositionResolver.TryGetBlockPos(player.CurrentEntitySelection.Entity, out pos))
        {
            return true;
        }

        if (player.CurrentBlockSelection?.Position != null)
        {
            pos = player.CurrentBlockSelection.Position.Copy();
            return true;
        }

        return player.CurrentEntitySelection?.Entity != null &&
               EntityPositionResolver.TryGetBlockPos(player.CurrentEntitySelection.Entity, out pos);
    }

    private static string NormalizeView(string? view) => view?.Trim().ToLowerInvariant() switch
    {
        "containers" => "containers",
        "entities" => "entities",
        "players" => "players",
        _ => "blocks"
    };

    private static string? NormalizeAction(string view, string? action)
    {
        var value = action?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) || value == "any") return null;

        return view switch
        {
            "containers" => value is "open" or "take" or "put" ? value : "container",
            "entities" => value is "entityspawn" or "entitydeath" or "entitydespawn" or "entityinteract" ? value : null,
            "players" => value is "blocks" or "containers" or "entities" ? value : null,
            _ => value is "break" or "place" or "interact" ? value : null
        };
    }

    private static string? TrimFilter(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string Title(string view) => view switch
    {
        "containers" => "Container history",
        "entities" => "Entity history",
        "players" => "Player history",
        _ => "Block history"
    };

    private static string FormatPlayerLine(LogEventRecord record, string? languageCode)
    {
        var actor = string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : record.PlayerName;
        var code = !string.IsNullOrWhiteSpace(record.BlockCode)
            ? record.BlockCode
            : Meta(record, "entityCode") ?? "unknown";

        var detail = record.Action switch
        {
            LogActionType.ContainerTake => $"took {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} from {code}",
            LogActionType.ContainerPut => $"put {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} into {code}",
            LogActionType.ContainerOpen => $"opened {code}",
            LogActionType.BlockBreak => $"broke {code}",
            LogActionType.BlockPlace => $"placed {code}",
            LogActionType.BlockUse => $"used {code}",
            LogActionType.EntityInteract => $"interacted with {Meta(record, "entityCode") ?? "entity"}",
            LogActionType.EntitySpawn => $"spawned {Meta(record, "entityCode") ?? "entity"}{CountSuffix(record)}",
            LogActionType.EntityDeath => $"{Meta(record, "entityCode") ?? "entity"} died",
            LogActionType.EntityDespawn => $"despawned {Meta(record, "entityCode") ?? "entity"}{CountSuffix(record)}",
            _ => record.Action.ToString()
        };

        return $"{IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc)} | {actor} | {detail} | {record.X},{record.Y},{record.Z} | {IngameTimeFormatter.LocalizeStoredStamp(record.IngameDate, languageCode)}";
    }

    private static string FormatLine(LogEventRecord record, BlockPos target, string? languageCode)
    {
        var actor = string.IsNullOrWhiteSpace(record.PlayerName) ? "Unknown" : record.PlayerName;
        var dx = record.X - target.X;
        var dz = record.Z - target.Z;
        var code = !string.IsNullOrWhiteSpace(record.BlockCode) ? record.BlockCode : Meta(record, "entityCode") ?? "unknown";

        var detail = record.Action switch
        {
            LogActionType.ContainerTake => $"took {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} from {code}",
            LogActionType.ContainerPut => $"put {Quantity(record)}x {Meta(record, "itemCode") ?? "unknown"} into {code}",
            LogActionType.ContainerOpen => $"opened {code}",
            LogActionType.BlockBreak => $"broke {code}",
            LogActionType.BlockPlace => $"placed {code}",
            LogActionType.BlockUse => $"used {code}",
            LogActionType.EntityInteract => $"interacted with {Meta(record, "entityCode") ?? "entity"}",
            LogActionType.EntitySpawn => $"spawned {Meta(record, "entityCode") ?? "entity"}{CountSuffix(record)}",
            LogActionType.EntityDeath => $"{Meta(record, "entityCode") ?? "entity"} died",
            LogActionType.EntityDespawn => $"despawned {Meta(record, "entityCode") ?? "entity"}{CountSuffix(record)}",
            _ => record.Action.ToString()
        };

        return $"{IngameTimeFormatter.FormatNewfoundlandTimestamp(record.TimestampUtc)} | {actor} | {detail} | {record.X},{record.Y},{record.Z} | ΔX/Z {dx:+#;-#;0}/{dz:+#;-#;0} | {IngameTimeFormatter.LocalizeStoredStamp(record.IngameDate, languageCode)}";
    }

    private static string? Meta(LogEventRecord record, string key) =>
        record.Meta != null && record.Meta.TryGetValue(key, out var value) ? value : null;

    private static int Quantity(LogEventRecord record) =>
        int.TryParse(Meta(record, "quantity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string CountSuffix(LogEventRecord record) =>
        long.TryParse(Meta(record, "eventCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 1
            ? $" x{count:N0}"
            : "";
}
