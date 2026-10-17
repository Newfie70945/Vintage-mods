using System;
using System.Linq;
using BlockLogger.Afk;
using BlockLogger.Reports;
using BlockLogger.Compatibility;
using BlockLogger.PlayerCommands;
using BlockLogger.Restarts;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.Admin;

public sealed class AdminCommands
{
    private readonly ICoreServerAPI api;
    private readonly AdminService admin;
    private readonly ReportsService? reports;
    private readonly PlayerCommandsService? playerCommands;
    private readonly Func<AutomatedRestartService?> getAutomatedRestarts;

    public AdminCommands(ICoreServerAPI api, AdminService admin, ReportsService? reports, PlayerCommandsService? playerCommands, Func<AutomatedRestartService?> getAutomatedRestarts)
    {
        this.api = api;
        this.admin = admin;
        this.reports = reports;
        this.playerCommands = playerCommands;
        this.getAutomatedRestarts = getAutomatedRestarts;
    }

    private bool HasTh3Essentials => api.ModLoader.Mods.Any(mod =>
        mod?.Info != null &&
        string.Equals(mod.Info.ModID, "th3essentials", StringComparison.OrdinalIgnoreCase));

    public void Register()
    {
        bool th3Compatibility = HasTh3Essentials;
        bool stratumCompatibility = RuntimeCompatibility.IsStratum;
        bool restartAlreadyOwned = RootCommandExists("restart");

        if (th3Compatibility)
        {
            api.Logger.Notification(
                "[ServerEssentials/Admin] Th3Essentials detected. Compatibility mode: root /restart and /shutdown are left to Th3Essentials. Player-command roots such as /back are registered only when free; NSE fallbacks remain available under /nse.");
        }

        if (stratumCompatibility)
        {
            api.Logger.Notification(
                "[ServerEssentials/Admin] Stratum runtime detected ({0}). Compatibility mode: NSE will not register root /restart; use Stratum /restart or /nse restart.",
                RuntimeCompatibility.StratumDetection);
        }
        else if (restartAlreadyOwned && !th3Compatibility)
        {
            api.Logger.Notification(
                "[ServerEssentials/Admin] Root /restart was already registered before NSE command setup. NSE will leave it untouched; /nse restart remains available.");
        }

#pragma warning disable CS0618
        SafeRegister("nse", "Newfies Server Essentials administration and player utilities", "spawn|msg|r|starterkit|setstarterkit|resetstarterkitusage|resetstarterkitusageall|back|home|sethome|delhome|homes|t2p|t2m|tpaccept|rtp|warp|warps|warpinfo|setwarp|delwarp|setwarpcost|setwarpcooldown|setwarpwarmup|setwarpmove|warpreload|player|report|reports|reportstatus|reportassign|reportnote|warn|warnings|unwarn|mute|unmute|muteinfo|tempban|baninfo|unban|note|history|freeze|unfreeze|goto|bring|sendback|smite|setjail|jail|unjail|jailinfo|invsee|invsnapshot|invrestore|invgive|invqty|invmove|invreplace|invremove|heal|feed|revive|ghost|seen|playtime|afk|afkinfo|online|laghere|cleanup|maintenance|shutdown|restart|restarttime|autorestart|undo", OnNse);
        SafeRegister("warn", "Warn a player", "<player> <reason>", (p,g,a)=>Warn(p,g,a), AdminService.PrivWarn);
        SafeRegister("warnings", "List player warnings", "<player>", (p,g,a)=>Warnings(p,g,a), AdminService.PrivWarn);
        SafeRegister("unwarn", "Remove a warning", "<player> <warningId>", (p,g,a)=>Unwarn(p,g,a), AdminService.PrivWarn);
        SafeRegister("tempban", "Temporarily ban a player", "<player> <30m|6h|3d|2w> <reason>", (p,g,a)=>TempBan(p,g,a), AdminService.PrivBan);
        SafeRegister("baninfo", "Show suite temporary-ban state", "<player>", (p,g,a)=>BanInfo(p,g,a), AdminService.PrivBan);
        SafeRegister("mute", "Mute a player", "<player> <duration|permanent> <reason>", (p,g,a)=>Mute(p,g,a), AdminService.PrivMute);
        SafeRegister("unmute", "Unmute a player", "<player>", (p,g,a)=>Unmute(p,g,a), AdminService.PrivMute);
        SafeRegister("muteinfo", "Show mute state", "<player>", (p,g,a)=>MuteInfo(p,g,a), AdminService.PrivMute);
        SafeRegister("freeze", "Freeze a player", "<player> [duration|permanent] [reason]", (p,g,a)=>Freeze(p,g,a), AdminService.PrivManage);
        SafeRegister("unfreeze", "Unfreeze a player", "<player>", (p,g,a)=>Unfreeze(p,g,a), AdminService.PrivManage);
        SafeRegister("goto", "Teleport to a player", "<player>", (p,g,a)=>Goto(p,g,a), AdminService.PrivTeleport);
        SafeRegister("bring", "Bring a player to you", "<player>", (p,g,a)=>Bring(p,g,a), AdminService.PrivTeleport);
        SafeRegister("tphere", "Bring a player to you", "<player>", (p,g,a)=>Bring(p,g,a), AdminService.PrivTeleport);
        SafeRegister("sendback", "Return a brought player", "<player>", (p,g,a)=>SendBack(p,g,a), AdminService.PrivTeleport);
        SafeRegister("invsee", "Read-only player inventory summary", "<player>", (p,g,a)=>InvSee(p,g,a), AdminService.PrivInventory);
        SafeRegister("invremove", "Remove one player inventory slot with audit/undo", "<player> <inventoryId> <slot> --confirm", (p,g,a)=>InvRemove(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("invsnapshot", "Create an inventory snapshot", "<player> [reason]", (p,g,a)=>InvSnapshot(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("invrestore", "Restore an inventory snapshot", "<player> <snapshotId> --confirm", (p,g,a)=>InvRestore(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("invgive", "Give an item/block with automatic snapshot", "<player> <code> <qty>", (p,g,a)=>InvGive(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("invqty", "Change one stack quantity with automatic snapshot", "<player> <inventoryId> <slot> <qty>", (p,g,a)=>InvQty(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("invmove", "Move/swap inventory slots with automatic snapshot", "<player> <fromInv> <fromSlot> <toInv> <toSlot>", (p,g,a)=>InvMove(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("invreplace", "Replace one inventory slot with automatic snapshot", "<player> <inventoryId> <slot> <code> <qty>", (p,g,a)=>InvReplace(p,g,a), AdminService.PrivInventoryEdit);
        SafeRegister("heal", "Heal a player", "<player>", (p,g,a)=>SimpleTarget(p,g,a,"heal"), AdminService.PrivManage);
        SafeRegister("feed", "Feed a player", "<player>", (p,g,a)=>SimpleTarget(p,g,a,"feed"), AdminService.PrivManage);
        SafeRegister("revive", "Revive a player", "<player>", (p,g,a)=>SimpleTarget(p,g,a,"revive"), AdminService.PrivManage);
        SafeRegister("smite", "Strike a player with lightning", "<player> [reason]", (p,g,a)=>Smite(p,g,a), AdminService.PrivSmite);
        SafeRegister("setjail", "Set the server jail cell to your current position", "", (p,g,a)=>SetJail(p,g), AdminService.PrivJail);
        SafeRegister("jail", "Jail a player for a duration", "<player> <duration|permanent> [reason]", (p,g,a)=>Jail(p,g,a), AdminService.PrivJail);
        SafeRegister("unjail", "Release a player from jail", "<player> [reason]", (p,g,a)=>Unjail(p,g,a), AdminService.PrivJail);
        SafeRegister("jailinfo", "Show a player jail sentence", "<player>", (p,g,a)=>JailInfo(p,g,a), AdminService.PrivJail);
        SafeRegister("ghost", "Toggle your admin ghost/spectator mode", "", (p,g,a)=>ReplyAction(p,g,"ghost",admin.ToggleGhost(p,"ingame")), AdminService.PrivManage);
        SafeRegister("seen", "Show when a player was last seen", "<player>", (p,g,a)=>Seen(p,g,a), AdminService.PrivView);
        SafeRegister("playtime", "Show tracked playtime", "<player>", (p,g,a)=>Playtime(p,g,a), AdminService.PrivView);
        SafeRegister("afkinfo", "Show player AFK/idle state", "<player>", (p,g,a)=>AfkInfo(p,g,a), AdminService.PrivView);
        SafeRegister("online", "List online players with ping", "", (p,g,a)=>Online(p,g), AdminService.PrivView);
        SafeRegister("history", "Show unified suite and BlockLogger player history", "<player>", (p,g,a)=>History(p,g,a), AdminService.PrivView);
        SafeRegister("note", "Manage private staff notes", "<player> add <text>|list|remove <id>", (p,g,a)=>Note(p,g,a), AdminService.PrivManage);
        if (reports != null)
        {
            try
            {
                var existingReport = api.ChatCommands.Get("report");
                if (existingReport == null)
                {
                    // Explicitly use the ordinary chat privilege so every normal
                    // player can submit reports. Do not require an admin/NSE privilege.
                    SafeRegister("report", "Report an issue to server staff", "<issue>", (p,g,a)=>Report(p,g,a), Privilege.chat);
                    reports.SetRootCommandOwnership("Newfies Server Essentials", true);
                    api.Logger.Notification("[ServerEssentials/Reports] Registered root /report command.");
                }
                else
                {
                    reports.SetRootCommandOwnership("Another registered provider", false);
                    api.Logger.Notification("[ServerEssentials/Reports] Root /report already exists. NSE will not replace it; /nse report remains available.");
                }
            }
            catch (Exception ex)
            {
                reports.SetRootCommandOwnership("Unknown (inspection failed)", false);
                api.Logger.Warning("[ServerEssentials/Reports] Could not inspect/register root /report: {0}: {1}", ex.GetType().Name, ex.Message);
            }
        }
        if (!th3Compatibility)
        {
            SafeRegister("shutdown", "Schedule a clean server shutdown", "<delay> [reason]|cancel", (p,g,a)=>Shutdown(p,g,a), AdminService.PrivServer);
        }

        // /restart is a convenience alias only. /nse restart is always available.
        // Stratum is a patched server runtime rather than a normal ModLoader mod, so
        // detect it at the process/assembly level and stay out of its command namespace.
        // Also leave any provider that registered /restart before NSE completely intact.
        if (!th3Compatibility && !stratumCompatibility && !restartAlreadyOwned)
        {
            SafeRegister("restart", "Schedule a clean shutdown for an external supervisor restart", "<delay> [reason]|cancel", (p,g,a)=>Restart(p,g,a), AdminService.PrivServer);
        }
        SafeRegister("nseundo", "Undo the most recent reversible suite admin action", "", (p,g,a)=>ReplyAction(p,g,"undo",admin.Undo(p.PlayerName)), AdminService.PrivManage);
        SafeRegister("laghere", "Scan nearby entities for lag investigation", "[radiusChunks]", (p,g,a)=>LagHere(p,g,a), AdminService.PrivDiagnostics);
        SafeRegister("maintenance", "Manage maintenance mode", "on [duration] [reason]|off|status", (p,g,a)=>Maintenance(p,g,a), AdminService.PrivServer);
#pragma warning restore CS0618
    }

    private bool RootCommandExists(string command)
    {
        try
        {
            return api.ChatCommands.Get(command) != null;
        }
        catch (Exception ex)
        {
            // Inspection failure should never crash suite startup. SafeRegister still
            // performs a second ownership check before any alias is registered.
            api.Logger.Warning(
                "[ServerEssentials/Admin] Could not inspect root command /{0}: {1}: {2}",
                command, ex.GetType().Name, ex.Message);
            return false;
        }
    }

    private void SafeRegister(string command, string description, string syntax, ServerChatCommandDelegate handler, string? requiredPrivilege = null)
    {
        try
        {
            if (api.ChatCommands.Get(command) != null)
            {
                api.Logger.Notification(
                    "[ServerEssentials/Admin] Root command /{0} is already owned by another provider. NSE skipped that alias; use /nse {0} where available.",
                    command);
                return;
            }

#pragma warning disable CS0618
            api.RegisterCommand(command, description, syntax, handler, requiredPrivilege);
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            api.Logger.Warning(
                "[ServerEssentials/Admin] Could not register root alias /{0}; leaving the existing provider untouched. {1}: {2}",
                command, ex.GetType().Name, ex.Message);
        }
    }

    private void OnNse(IServerPlayer player, int groupId, CmdArgs args)
    {
        string op = (args.PopWord() ?? "help").ToLowerInvariant();
        switch (op)
        {
            case "spawn":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleSpawn(player, groupId, args);
                break;
            case "msg":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleMsg(player, groupId, args);
                break;
            case "r": case "reply":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleReply(player, groupId, args);
                break;
            case "starterkit":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleStarterKit(player, groupId, args);
                break;
            case "setstarterkit":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleSetStarterKit(player, groupId, args);
                });
                break;
            case "resetstarterkitusage":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleResetStarterKitUsage(player, groupId, args);
                });
                break;
            case "resetstarterkitusageall":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleResetStarterKitUsageAll(player, groupId, args);
                });
                break;
            case "back":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleBack(player, groupId, args);
                break;
            case "home":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleHome(player, groupId, args);
                break;
            case "sethome":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleSetHome(player, groupId, args);
                break;
            case "delhome":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleDeleteHome(player, groupId, args);
                break;
            case "homes":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleHomes(player, groupId, args);
                break;
            case "t2p":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleT2p(player, groupId, args);
                break;
            case "t2m":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleT2m(player, groupId, args);
                break;
            case "tpaccept":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleTpAccept(player, groupId, args);
                break;
            case "rtp":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleRtp(player, groupId, args);
                break;
            case "warp":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleWarp(player, groupId, args);
                break;
            case "warps":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleWarps(player, groupId, args);
                break;
            case "warpinfo":
                if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                else playerCommands.HandleWarpInfo(player, groupId, args);
                break;
            case "setwarp":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleSetWarp(player, groupId, args);
                });
                break;
            case "delwarp":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleDeleteWarp(player, groupId, args);
                });
                break;
            case "setwarpcost":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleSetWarpCost(player, groupId, args);
                });
                break;
            case "setwarpcooldown":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleSetWarpCooldown(player, groupId, args);
                });
                break;
            case "setwarpwarmup":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleSetWarpWarmup(player, groupId, args);
                });
                break;
            case "setwarpmove":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleSetWarpMovement(player, groupId, args);
                });
                break;
            case "warpreload":
                Require(player, groupId, Privilege.controlserver, () =>
                {
                    if (playerCommands == null) Reply(player, groupId, "Player Commands feature is disabled.", false);
                    else playerCommands.HandleReloadWarps(player, groupId, args);
                });
                break;
            case "restarttime":
            {
                var restarts = getAutomatedRestarts();
                if (restarts == null) Reply(player, groupId, "Automated restart service is unavailable.", false);
                else restarts.HandleRestartTime(player, groupId, args);
                break;
            }
            case "autorestart":
                Require(player, groupId, AdminService.PrivServer, () => AutoRestart(player, groupId, args));
                break;
            case "player": Require(player,groupId,AdminService.PrivView,()=>PlayerInfo(player,groupId,args)); break;
            case "report": Report(player,groupId,args); break;
            case "reports": Require(player,groupId,AdminService.PrivReports,()=>ReportsList(player,groupId,args)); break;
            case "reportstatus": Require(player,groupId,AdminService.PrivReports,()=>ReportStatus(player,groupId,args)); break;
            case "reportassign": Require(player,groupId,AdminService.PrivReports,()=>ReportAssign(player,groupId,args)); break;
            case "reportnote": Require(player,groupId,AdminService.PrivReports,()=>ReportNote(player,groupId,args)); break;
            case "warn": Require(player,groupId,AdminService.PrivWarn,()=>Warn(player,groupId,args)); break;
            case "warnings": Require(player,groupId,AdminService.PrivWarn,()=>Warnings(player,groupId,args)); break;
            case "unwarn": Require(player,groupId,AdminService.PrivWarn,()=>Unwarn(player,groupId,args)); break;
            case "mute": Require(player,groupId,AdminService.PrivMute,()=>Mute(player,groupId,args)); break;
            case "unmute": Require(player,groupId,AdminService.PrivMute,()=>Unmute(player,groupId,args)); break;
            case "muteinfo": Require(player,groupId,AdminService.PrivMute,()=>MuteInfo(player,groupId,args)); break;
            case "tempban": Require(player,groupId,AdminService.PrivBan,()=>TempBan(player,groupId,args)); break;
            case "baninfo": Require(player,groupId,AdminService.PrivBan,()=>BanInfo(player,groupId,args)); break;
            case "unban": Require(player,groupId,AdminService.PrivBan,()=>Unban(player,groupId,args)); break;
            case "note": Require(player,groupId,AdminService.PrivManage,()=>Note(player,groupId,args)); break;
            case "history": Require(player,groupId,AdminService.PrivView,()=>History(player,groupId,args)); break;
            case "freeze": Require(player,groupId,AdminService.PrivManage,()=>Freeze(player,groupId,args)); break;
            case "unfreeze": Require(player,groupId,AdminService.PrivManage,()=>Unfreeze(player,groupId,args)); break;
            case "goto": Require(player,groupId,AdminService.PrivTeleport,()=>Goto(player,groupId,args)); break;
            case "bring": case "tphere": Require(player,groupId,AdminService.PrivTeleport,()=>Bring(player,groupId,args)); break;
            case "sendback": Require(player,groupId,AdminService.PrivTeleport,()=>SendBack(player,groupId,args)); break;
            case "smite": Require(player,groupId,AdminService.PrivSmite,()=>Smite(player,groupId,args)); break;
            case "setjail": Require(player,groupId,AdminService.PrivJail,()=>SetJail(player,groupId)); break;
            case "jail": Require(player,groupId,AdminService.PrivJail,()=>Jail(player,groupId,args)); break;
            case "unjail": Require(player,groupId,AdminService.PrivJail,()=>Unjail(player,groupId,args)); break;
            case "jailinfo": Require(player,groupId,AdminService.PrivJail,()=>JailInfo(player,groupId,args)); break;
            case "invsee": Require(player,groupId,AdminService.PrivInventory,()=>InvSee(player,groupId,args)); break;
            case "invsnapshot": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvSnapshot(player,groupId,args)); break;
            case "invrestore": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvRestore(player,groupId,args)); break;
            case "invgive": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvGive(player,groupId,args)); break;
            case "invqty": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvQty(player,groupId,args)); break;
            case "invmove": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvMove(player,groupId,args)); break;
            case "invreplace": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvReplace(player,groupId,args)); break;
            case "invremove": Require(player,groupId,AdminService.PrivInventoryEdit,()=>InvRemove(player,groupId,args)); break;
            case "heal": Require(player,groupId,AdminService.PrivManage,()=>SimpleTarget(player,groupId,args,"heal")); break;
            case "feed": Require(player,groupId,AdminService.PrivManage,()=>SimpleTarget(player,groupId,args,"feed")); break;
            case "revive": Require(player,groupId,AdminService.PrivManage,()=>SimpleTarget(player,groupId,args,"revive")); break;
            case "ghost": Require(player,groupId,AdminService.PrivManage,()=>ReplyAction(player,groupId,"ghost",admin.ToggleGhost(player,"ingame"))); break;
            case "seen": Require(player,groupId,AdminService.PrivView,()=>Seen(player,groupId,args)); break;
            case "playtime": Require(player,groupId,AdminService.PrivView,()=>Playtime(player,groupId,args)); break;
            case "afk": Require(player,groupId,NewfiesAfkModSystem.AfkPrivilegeCode,()=>AfkToggle(player,groupId)); break;
            case "afkinfo": Require(player,groupId,AdminService.PrivView,()=>AfkInfo(player,groupId,args)); break;
            case "online": Require(player,groupId,AdminService.PrivView,()=>Online(player,groupId)); break;
            case "laghere": Require(player,groupId,AdminService.PrivDiagnostics,()=>LagHere(player,groupId,args)); break;
            case "cleanup": Require(player,groupId,AdminService.PrivCleanup,()=>Cleanup(player,groupId,args)); break;
            case "maintenance": Require(player,groupId,AdminService.PrivServer,()=>Maintenance(player,groupId,args)); break;
            case "shutdown": Require(player,groupId,AdminService.PrivServer,()=>Shutdown(player,groupId,args)); break;
            case "restart": Require(player,groupId,AdminService.PrivServer,()=>Restart(player,groupId,args)); break;
            case "undo": Require(player,groupId,AdminService.PrivManage,()=>ReplyAction(player,groupId,"undo",admin.Undo(player.PlayerName))); break;
            default: SendHelp(player,groupId); break;
        }
    }

    private void Report(IServerPlayer p, int g, CmdArgs a)
    {
        string issue = a.PopAll();
        if (string.IsNullOrWhiteSpace(issue))
        {
            Reply(p, g, "Usage: /report <issue>", false);
            return;
        }
        if (reports == null) { Reply(p, g, "Reports feature is disabled.", false); return; }
        var result = reports.CreateReport(p, issue);
        Reply(p, g, result.Message, result.Success);
    }

    private void ReportsList(IServerPlayer p, int g, CmdArgs a)
    {
        string filter = (a.PopWord() ?? "open").ToLowerInvariant();
        if (filter is not ("open" or "investigating" or "resolved" or "all"))
        {
            Reply(p, g, "Usage: /nse reports [open|investigating|resolved|all]", false);
            return;
        }
        if (reports == null) { Reply(p, g, "Reports feature is disabled.", false); return; }
        var snapshot = reports.GetSnapshot(filter, 25);
        Reply(p, g, $"Reports: open={snapshot.OpenCount}, investigating={snapshot.InvestigatingCount}, resolved={snapshot.ResolvedCount}, total={snapshot.TotalCount}.", true);
        if (snapshot.Reports.Count == 0)
        {
            Reply(p, g, $"No {filter} reports.", true);
            return;
        }
        foreach (var r in snapshot.Reports)
        {
            string assigned = string.IsNullOrWhiteSpace(r.AssignedStaff) ? "unassigned" : r.AssignedStaff;
            Reply(p, g, $"#{r.Id} [{r.Status}] {r.ReporterName} | {r.Reason} | {assigned} | {DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedUnixMilliseconds):u}", true);
        }
    }

    private void ReportStatus(IServerPlayer p, int g, CmdArgs a)
    {
        long id = a.PopLong(0) ?? 0;
        string status = a.PopWord() ?? "";
        if (id <= 0 || string.IsNullOrWhiteSpace(status))
        {
            Reply(p, g, "Usage: /nse reportstatus <id> <open|investigating|resolved> [note]", false);
            return;
        }
        if (reports == null) { Reply(p, g, "Reports feature is disabled.", false); return; }
        string message = reports.SetStatus(id, status, p.PlayerName, a.PopAll());
        Reply(p, g, message, ReportMutationSucceeded(message));
    }

    private void ReportAssign(IServerPlayer p, int g, CmdArgs a)
    {
        long id = a.PopLong(0) ?? 0;
        string staff = a.PopAll().Trim();
        if (id <= 0 || string.IsNullOrWhiteSpace(staff))
        {
            Reply(p, g, "Usage: /nse reportassign <id> <me|staff name|clear>", false);
            return;
        }
        if (staff.Equals("me", StringComparison.OrdinalIgnoreCase)) staff = p.PlayerName;
        else if (staff.Equals("clear", StringComparison.OrdinalIgnoreCase) || staff.Equals("none", StringComparison.OrdinalIgnoreCase)) staff = "";
        if (reports == null) { Reply(p, g, "Reports feature is disabled.", false); return; }
        string message = reports.Assign(id, staff, p.PlayerName);
        Reply(p, g, message, ReportMutationSucceeded(message));
    }

    private void ReportNote(IServerPlayer p, int g, CmdArgs a)
    {
        long id = a.PopLong(0) ?? 0;
        string note = a.PopAll();
        if (id <= 0 || string.IsNullOrWhiteSpace(note))
        {
            Reply(p, g, "Usage: /nse reportnote <id> <private staff note>", false);
            return;
        }
        if (reports == null) { Reply(p, g, "Reports feature is disabled.", false); return; }
        string message = reports.AddNote(id, p.PlayerName, note);
        Reply(p, g, message, ReportMutationSucceeded(message));
    }

    private static bool ReportMutationSucceeded(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return !message.Contains("was not found", StringComparison.OrdinalIgnoreCase)
            && !message.StartsWith("Status must", StringComparison.OrdinalIgnoreCase)
            && !message.StartsWith("Note text is required", StringComparison.OrdinalIgnoreCase)
            && !message.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private void PlayerInfo(IServerPlayer player, int groupId, CmdArgs args)
    {
        string target = args.PopWord() ?? player.PlayerName;
        var p = admin.GetPlayer(target);
        if (p == null) { Reply(player,groupId,"Player not found.",false); return; }
        Reply(player,groupId,$"{p.Name} | {(p.Online?"Online":"Offline")} | UID={p.PlayerUid} | Role={p.Role} | Mode={p.GameMode} | Ping={p.PingMilliseconds}ms | Pos={p.Position} | Health={p.Health:0.#}/{p.MaxHealth:0.#} | Warnings={p.ActiveWarnings} | Muted={p.Muted} | Banned={p.Banned} | Frozen={p.Frozen} | Playtime={FormatSeconds(p.TotalPlaySeconds)}",true);
    }

    private void Warn(IServerPlayer p,int g,CmdArgs a){ string t=a.PopWord()??""; ReplyAction(p,g,"warn",admin.Warn(p.PlayerName,t,a.PopAll())); }
    private void Warnings(IServerPlayer p,int g,CmdArgs a){ string t=a.PopWord()??""; var w=admin.GetWarnings(t); if(w.Count==0){Reply(p,g,"No warnings found.",true);return;} foreach(var x in w.Take(20)) Reply(p,g,$"#{x.Id} {(x.Removed?"REMOVED":"ACTIVE")} {DateTimeOffset.FromUnixTimeMilliseconds(x.UnixMilliseconds):u} by {x.IssuedBy}: {x.Reason}",true); }
    private void Unwarn(IServerPlayer p,int g,CmdArgs a){ string t=a.PopWord()??""; long id=a.PopLong(0)??0; ReplyAction(p,g,"unwarn",admin.RemoveWarning(p.PlayerName,t,id)); }
    private void TempBan(IServerPlayer p,int g,CmdArgs a){ string t=a.PopWord()??""; string d=a.PopWord()??""; ReplyAction(p,g,"tempban",admin.TempBan(p.PlayerName,t,d,a.PopAll())); }
    private void Unban(IServerPlayer p,int g,CmdArgs a){ ReplyAction(p,g,"unban",admin.Unban(p.PlayerName,a.PopWord()??"")); }
    private void BanInfo(IServerPlayer p,int g,CmdArgs a){ var x=admin.GetPlayer(a.PopWord()??""); if(x==null){Reply(p,g,"Player not found.",false);return;} Reply(p,g,x.Banned?$"{x.Name} is temporarily banned until {DateTimeOffset.FromUnixTimeMilliseconds(x.BanUntilUnixMilliseconds):u}." : $"{x.Name} has no active suite temporary ban.",true); }
    private void Mute(IServerPlayer p,int g,CmdArgs a){ string t=a.PopWord()??""; string d=a.PopWord()??""; ReplyAction(p,g,"mute",admin.Mute(p.PlayerName,t,d,a.PopAll())); }
    private void Unmute(IServerPlayer p,int g,CmdArgs a){ ReplyAction(p,g,"unmute",admin.Unmute(p.PlayerName,a.PopWord()??"")); }
    private void MuteInfo(IServerPlayer p,int g,CmdArgs a){ var x=admin.GetPlayer(a.PopWord()??""); if(x==null){Reply(p,g,"Player not found.",false);return;} Reply(p,g,x.Muted?$"{x.Name} is muted{(x.MuteUntilUnixMilliseconds>0?$" until {DateTimeOffset.FromUnixTimeMilliseconds(x.MuteUntilUnixMilliseconds):u}":" permanently")}." : $"{x.Name} is not muted.",true); }

    private void Note(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??""; string op=(a.PopWord()??"list").ToLowerInvariant();
        if(op=="add") ReplyAction(p,g,"note",admin.AddNote(p.PlayerName,target,a.PopAll()));
        else if(op=="remove") ReplyAction(p,g,"note-remove",admin.RemoveNote(p.PlayerName,target,a.PopLong(0)??0));
        else { var notes=admin.GetNotes(target); if(notes.Count==0){Reply(p,g,"No staff notes.",true);return;} foreach(var n in notes.Take(20)) Reply(p,g,$"#{n.Id} {DateTimeOffset.FromUnixTimeMilliseconds(n.UnixMilliseconds):u} {n.Author}: {n.Text}",true); }
    }

    private void History(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??p.PlayerName;
        _ = HistoryAsync(p,g,target);
    }

    private async System.Threading.Tasks.Task HistoryAsync(IServerPlayer p,int g,string target)
    {
        try
        {
            var adminHistory=admin.GetHistory(target,50);
            var blockHistory=await admin.GetBlockHistoryAsync(target,50).ConfigureAwait(false);
            var rows=new System.Collections.Generic.List<(DateTime Time,string Text)>();
            rows.AddRange(adminHistory.Select(x=>(DateTimeOffset.FromUnixTimeMilliseconds(x.UnixMilliseconds).UtcDateTime,$"ADMIN {x.Action} by {x.Actor}: {x.Detail}")));
            rows.AddRange(blockHistory.Select(x=>(x.TimestampUtc.ToUniversalTime(),$"BLOCKLOG {x.Action}: {x.BlockCode} @ {x.X},{x.Y},{x.Z}")));
            var ordered=rows.OrderByDescending(x=>x.Time).Take(30).ToList();
            api.Event.EnqueueMainThreadTask(()=>
            {
                if(ordered.Count==0){Reply(p,g,"No suite or BlockLogger history found.",true);return;}
                foreach(var row in ordered) Reply(p,g,$"{row.Time:u} {row.Text}",true);
            },"nse-admin-history");
        }
        catch(Exception ex)
        {
            api.Event.EnqueueMainThreadTask(()=>Reply(p,g,$"History query failed: {ex.Message}",false),"nse-admin-history-error");
        }
    }
    private void Freeze(IServerPlayer p,int g,CmdArgs a){ string t=a.PopWord()??""; string d=a.PopWord()??"permanent"; ReplyAction(p,g,"freeze",admin.Freeze(p.PlayerName,t,d,a.PopAll())); }
    private void Unfreeze(IServerPlayer p,int g,CmdArgs a){ ReplyAction(p,g,"unfreeze",admin.Unfreeze(p.PlayerName,a.PopWord()??"")); }
    private void Goto(IServerPlayer p,int g,CmdArgs a){ ReplyAction(p,g,"goto",admin.Goto(p,a.PopWord()??"")); }
    private void Bring(IServerPlayer p,int g,CmdArgs a){ ReplyAction(p,g,"bring",admin.Bring(p,a.PopWord()??"")); }
    private void SendBack(IServerPlayer p,int g,CmdArgs a){ ReplyAction(p,g,"sendback",admin.SendBack(p.PlayerName,a.PopWord()??"")); }
    private void Smite(IServerPlayer p,int g,CmdArgs a)
    {
        string target = a.PopWord() ?? "";
        if (string.IsNullOrWhiteSpace(target)) { Reply(p,g,"Usage: /smite <player> [reason]",false); return; }
        string result = admin.Smite(p.PlayerName, target, a.PopAll(), "ingame");
        // Smite uses a plain command response. The generic action formatter can produce a chat payload
        // that some clients render incorrectly after the lightning effect.
        bool success = AdminService.IsSuccessfulActionResult("smite", result);
        Reply(p, g, result, success);
    }

    private void SetJail(IServerPlayer p,int g)
    {
        string result = admin.SetJailPosition(p, "ingame");
        ReplyAction(p,g,"setjail",result);
    }

    private void Jail(IServerPlayer p,int g,CmdArgs a)
    {
        string target = a.PopWord() ?? "";
        string duration = a.PopWord() ?? "";
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(duration))
        {
            Reply(p,g,"Usage: /jail <player> <10m|2h|3d|permanent> [reason]",false);
            return;
        }
        string result = admin.Jail(p.PlayerName, target, duration, a.PopAll(), "ingame");
        ReplyAction(p,g,"jail",result);
    }

    private void Unjail(IServerPlayer p,int g,CmdArgs a)
    {
        string target = a.PopWord() ?? "";
        if (string.IsNullOrWhiteSpace(target)) { Reply(p,g,"Usage: /unjail <player> [reason]",false); return; }
        string result = admin.Unjail(p.PlayerName, target, a.PopAll(), "ingame");
        ReplyAction(p,g,"unjail",result);
    }

    private void JailInfo(IServerPlayer p,int g,CmdArgs a)
    {
        string target = a.PopWord() ?? "";
        if (string.IsNullOrWhiteSpace(target)) { Reply(p,g,"Usage: /jailinfo <player>",false); return; }
        string result = admin.JailInfo(target);
        bool success = !result.Contains("not jailed", StringComparison.OrdinalIgnoreCase) &&
                       !result.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
        Reply(p,g,result,success);
    }

    private void InvSee(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??""; var all=admin.GetInventory(target); var slots=all.Where(x=>!x.Empty).ToList();
        if(all.Count==0){Reply(p,g,"Player must be online or inventory is unavailable.",false);return;}
        Reply(p,g,$"Inventory for {target}: {slots.Count} occupied / {all.Count} total slots.",true);
        foreach(var grp in slots.GroupBy(x=>x.Inventory))
            Reply(p,g,$"{grp.Key}: {string.Join(", ",grp.Take(20).Select(x=>$"[{x.Slot}] {x.StackSize}x {x.Code}"))}{(grp.Count()>20?" ...":"")}",true);
    }

    private void InvRemove(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??"";
        string inventory=a.PopWord()??"";
        int slot=a.PopInt(-1)??-1;
        string confirm=a.PopWord()??"";
        if(!string.Equals(confirm,"--confirm",StringComparison.OrdinalIgnoreCase))
        {
            Reply(p,g,"Usage: /invremove <player> <inventoryId> <slot> --confirm",false);
            return;
        }
        ReplyAction(p,g,"inventory-remove",admin.RemoveInventorySlot(p.PlayerName,target,inventory,slot));
    }


    private void InvSnapshot(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??"";
        string reason=a.PopAll();
        ReplyAction(p,g,"inventory-snapshot",admin.CreateInventorySnapshot(p.PlayerName,target,string.IsNullOrWhiteSpace(reason)?"Manual in-game snapshot":reason));
    }

    private void InvRestore(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??"";
        long id=a.PopLong(0)??0;
        string confirm=a.PopWord()??"";
        if(!string.Equals(confirm,"--confirm",StringComparison.OrdinalIgnoreCase))
        {
            Reply(p,g,"Usage: /invrestore <player> <snapshotId> --confirm",false); return;
        }
        ReplyAction(p,g,"inventory-restore",admin.RestoreInventorySnapshot(p.PlayerName,target,id));
    }

    private void InvGive(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??""; string code=a.PopWord()??""; int qty=a.PopInt(1)??1;
        ReplyAction(p,g,"inventory-give",admin.GiveInventoryItem(p.PlayerName,target,code,qty));
    }

    private void InvQty(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??""; string inv=a.PopWord()??""; int slot=a.PopInt(-1)??-1; int qty=a.PopInt(-1)??-1;
        ReplyAction(p,g,"inventory-quantity",admin.SetInventoryQuantity(p.PlayerName,target,inv,slot,qty));
    }

    private void InvMove(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??""; string fromInv=a.PopWord()??""; int fromSlot=a.PopInt(-1)??-1; string toInv=a.PopWord()??""; int toSlot=a.PopInt(-1)??-1;
        ReplyAction(p,g,"inventory-move",admin.MoveInventorySlot(p.PlayerName,target,fromInv,fromSlot,toInv,toSlot));
    }

    private void InvReplace(IServerPlayer p,int g,CmdArgs a)
    {
        string target=a.PopWord()??""; string inv=a.PopWord()??""; int slot=a.PopInt(-1)??-1; string code=a.PopWord()??""; int qty=a.PopInt(1)??1;
        ReplyAction(p,g,"inventory-replace",admin.ReplaceInventorySlot(p.PlayerName,target,inv,slot,code,qty));
    }

    private void SimpleTarget(IServerPlayer p,int g,CmdArgs a,string op)
    {
        string t=a.PopWord()??"";
        string msg=op switch { "heal"=>admin.Heal(p.PlayerName,t), "feed"=>admin.Feed(p.PlayerName,t), _=>admin.Revive(p.PlayerName,t) };
        ReplyAction(p,g,op,msg);
    }

    private void Seen(IServerPlayer p,int g,CmdArgs a){ var x=admin.GetPlayer(a.PopWord()??""); if(x==null){Reply(p,g,"Player not found.",false);return;} Reply(p,g,x.Online?$"{x.Name} is online now." : $"{x.Name} was last seen {DateTimeOffset.FromUnixTimeMilliseconds(x.LastSeenUnixMilliseconds):u}.",true); }
    private void Playtime(IServerPlayer p,int g,CmdArgs a){ var x=admin.GetPlayer(a.PopWord()??""); if(x==null){Reply(p,g,"Player not found.",false);return;} Reply(p,g,$"{x.Name}: tracked playtime {FormatSeconds(x.TotalPlaySeconds)}; current session {FormatSeconds(x.SessionSeconds)}.",true); }
    private void AfkToggle(IServerPlayer p, int g)
    {
        var afk = NewfiesAfkModSystem.Current;
        if (afk == null)
        {
            Reply(p, g, "AFK feature is disabled or unavailable. Enable the afk feature gate and restart the server.", false);
            return;
        }

        var result = afk.ToggleForPlayer(p);
        Reply(p, g, result.Message, result.Success);
    }

    private void AfkInfo(IServerPlayer p,int g,CmdArgs a)
    {
        var x=admin.GetPlayer(a.PopWord()??"");
        if(x==null){Reply(p,g,"Player not found.",false);return;}
        if(!x.Online){Reply(p,g,$"{x.Name} is offline.",true);return;}
        bool explicitAfk = NewfiesAfkModSystem.Current?.IsAfk(x.PlayerUid) == true;
        string mode = explicitAfk ? "AFK (/afk active)" : (x.Afk ? "idle/AFK" : "active");
        Reply(p,g,$"{x.Name}: {mode} ({FormatSeconds(x.IdleSeconds)} since movement/chat activity).",true);
    }
    private void Online(IServerPlayer p,int g){ var list=admin.GetPlayers().Where(x=>x.Online).ToList(); Reply(p,g,$"Online ({list.Count}): {string.Join(", ",list.Select(x=>$"{x.Name}({x.PingMilliseconds}ms)"))}",true); }

    private void LagHere(IServerPlayer p,int g,CmdArgs a)
    {
        int r=Math.Clamp(a.PopInt(3)??3,1,12); var s=admin.ScanLagHere(p,r);
        Reply(p,g,$"LagHere {r} chunks: entities={s.Entities}, droppedItems={s.DroppedItems}, players={s.Players}, serverChunks={s.LoadedChunks}, delay={s.MainThreadDelayMs:0.#}ms, CPU={s.CpuPercent:0.#}%, BLQueue={s.BlockLoggerQueue}. Top: {string.Join(", ",s.TopEntityTypes.Select(x=>$"{x.Key}={x.Value}"))}",true);
    }

    private void Cleanup(IServerPlayer p,int g,CmdArgs a)
    {
        string kind=(a.PopWord()??"scan").ToLowerInvariant();
        if(kind=="scan")
        {
            var s=admin.ScanCleanup();
            Reply(p,g,$"Cleanup scan: loaded={s.LoadedEntities}, creatures={s.Creatures}, inanimate={s.InanimateEntities}, dead/despawning={s.DeadOrDespawning}, droppedItems={s.DroppedItems}. Use /nse cleanup drops --confirm to remove only dropped items.",true);
            return;
        }
        if(kind=="drops")
        {
            bool confirmed=string.Equals(a.PopWord(),"--confirm",StringComparison.OrdinalIgnoreCase);
            var s=admin.CleanupDrops(!confirmed);
            admin.Audit(p.PlayerName,confirmed?"cleanup-drops":"cleanup-drops-scan","","server",confirmed?$"Removed {s.Removed} dropped item entities":$"Dry run found {s.DroppedItems} dropped item entities","ingame");
            Reply(p,g,confirmed?$"Removed {s.Removed} dropped item entities." : $"Dry run: would remove {s.DroppedItems} dropped item entities. Re-run with --confirm.",true);
            return;
        }
        Reply(p,g,"Usage: /nse cleanup scan | /nse cleanup drops --confirm",false);
    }

    private void AutoRestart(IServerPlayer p, int g, CmdArgs a)
    {
        var restarts = getAutomatedRestarts();
        if (restarts == null) { Reply(p, g, "Automated restart service is unavailable.", false); return; }
        string op = (a.PopWord() ?? "status").Trim().ToLowerInvariant();
        if (op == "status") { Reply(p, g, restarts.StatusText(), true); return; }
        if (op == "on" || op == "enable") { Reply(p, g, restarts.SetEnabled(true), true); return; }
        if (op == "off" || op == "disable") { Reply(p, g, restarts.SetEnabled(false), true); return; }
        if (op == "reload") { Reply(p, g, restarts.Reload(), true); return; }
        if (op == "skip") { Reply(p, g, restarts.SkipNext(p.PlayerName), true); return; }
        Reply(p, g, "Usage: /nse autorestart status|on|off|reload|skip", false);
    }

    private void Maintenance(IServerPlayer p,int g,CmdArgs a)
    {
        string op=(a.PopWord()??"status").ToLowerInvariant();
        if(op=="status"){ var m=admin.GetMaintenance(); Reply(p,g,$"Maintenance={m.Enabled}, reason={m.Reason}{(m.Until>0?$", until={DateTimeOffset.FromUnixTimeMilliseconds(m.Until):u}":"")}",true); return; }
        if(op=="off"){ ReplyAction(p,g,"maintenance",admin.SetMaintenance(p.PlayerName,false,"")); return; }
        if(op=="on")
        {
            string maybe=a.PopWord()??""; long ms=0; string reason;
            if(AdminService.TryParseDuration(maybe,out ms,out bool perm) && !perm) reason=a.PopAll();
            else { reason=(maybe+" "+a.PopAll()).Trim(); ms=0; }
            ReplyAction(p,g,"maintenance",admin.SetMaintenance(p.PlayerName,true,reason,ms)); return;
        }
        Reply(p,g,"Usage: /nse maintenance on [duration] [reason] | off | status",false);
    }

    private void Shutdown(IServerPlayer p,int g,CmdArgs a)
    {
        string delay=a.PopWord()??"";
        if(delay.Equals("cancel",StringComparison.OrdinalIgnoreCase)){ReplyAction(p,g,"shutdown-cancel",admin.CancelShutdown(p.PlayerName));return;}
        if(!AdminService.TryParseDuration(delay,out long ms,out bool perm)||perm){Reply(p,g,"Usage: /nse shutdown <10m|30s|...> [reason] | cancel",false);return;}
        ReplyAction(p,g,"shutdown",admin.ScheduleShutdown(p.PlayerName,ms,a.PopAll()));
    }

    private void Restart(IServerPlayer p,int g,CmdArgs a)
    {
        string delay=a.PopWord()??"";
        if(delay.Equals("cancel",StringComparison.OrdinalIgnoreCase)){ReplyAction(p,g,"shutdown-cancel",admin.CancelShutdown(p.PlayerName));return;}
        if(!AdminService.TryParseDuration(delay,out long ms,out bool perm)||perm){Reply(p,g,"Usage: /nse restart <10m|30s|...> [reason] | cancel",false);return;}
        ReplyAction(p,g,"restart",admin.ScheduleRestart(p.PlayerName,ms,a.PopAll()));
    }

    private void Require(IServerPlayer p,int g,string privilege,Action action)
    {
        if(p.HasPrivilege(privilege)||p.HasPrivilege(Privilege.root)||p.HasPrivilege(Privilege.controlserver)){action();return;}
        Reply(p,g,$"Missing privilege: {privilege}",false);
    }

    private void SendHelp(IServerPlayer p,int g)
    {
        Reply(p,g,"/nse spawn | /nse msg <player> <message> | /nse r <message> | /nse starterkit | /nse back | /nse home [name] | /nse sethome [name] | /nse delhome [name] | /nse homes | /nse t2p <player> | /nse t2m <player> | /nse tpaccept | /nse rtp | /nse warp <name> | /nse warps | /nse warpinfo <name> | report <issue> | reports [open|investigating|resolved|all] | player <name> | warn/warnings/unwarn | mute/unmute | tempban/unban | freeze/unfreeze | goto/bring/sendback | smite <player> [reason] | setjail | jail <player> <duration> [reason] | unjail <player> | jailinfo <player> | ghost | invsee/... | seen/playtime/afk/afkinfo/online | laghere | cleanup | maintenance | shutdown/restart | restarttime | autorestart status|on|off|reload|skip | undo",true);
    }

    private static string FormatSeconds(long seconds)
    {
        var t=TimeSpan.FromSeconds(Math.Max(0,seconds));
        return t.TotalDays>=1?$"{t.TotalDays:0.#}d":t.TotalHours>=1?$"{t.TotalHours:0.#}h":$"{t.TotalMinutes:0.#}m";
    }

    private static void ReplyAction(IServerPlayer p, int g, string action, string message)
    {
        bool success = AdminService.IsSuccessfulActionResult(action, message);
        string prefix = success ? "Success: " : "Failed: ";
        Reply(p, g, prefix + message, success);
    }

    private static void Reply(IServerPlayer p,int g,string msg,bool success) => p.SendMessage(g,msg,success?EnumChatType.CommandSuccess:EnumChatType.CommandError);
}
