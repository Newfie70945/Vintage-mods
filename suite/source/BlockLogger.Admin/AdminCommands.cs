using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlockLogger.Afk;
using BlockLogger.Compatibility;
using BlockLogger.Models;
using BlockLogger.PlayerCommands;
using BlockLogger.Reports;
using BlockLogger.Restarts;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Admin;

public sealed class AdminCommands
{
	private readonly ICoreServerAPI api;

	private readonly AdminService admin;

	private readonly ReportsService? reports;

	private readonly PlayerCommandsService? playerCommands;

	private readonly Func<AutomatedRestartService?> getAutomatedRestarts;

	private bool HasTh3Essentials => api.ModLoader.Mods.Any((Mod mod) => mod?.Info != null && string.Equals(mod.Info.ModID, "th3essentials", StringComparison.OrdinalIgnoreCase));

	public AdminCommands(ICoreServerAPI api, AdminService admin, ReportsService? reports, PlayerCommandsService? playerCommands, Func<AutomatedRestartService?> getAutomatedRestarts)
	{
		this.api = api;
		this.admin = admin;
		this.reports = reports;
		this.playerCommands = playerCommands;
		this.getAutomatedRestarts = getAutomatedRestarts;
	}

	public void Register()
	{
		bool hasTh3Essentials = HasTh3Essentials;
		bool isStratum = RuntimeCompatibility.IsStratum;
		bool flag = RootCommandExists("restart");
		if (hasTh3Essentials)
		{
			api.Logger.Notification("[ServerEssentials/Admin] Th3Essentials detected. Compatibility mode: root /restart and /shutdown are left to Th3Essentials. Player-command roots such as /back are registered only when free; NSE fallbacks remain available under /nse.");
		}
		if (isStratum)
		{
			api.Logger.Notification("[ServerEssentials/Admin] Stratum runtime detected ({0}). Compatibility mode: NSE will not register root /restart; use Stratum /restart or /nse restart.", RuntimeCompatibility.StratumDetection);
		}
		else if (flag && !hasTh3Essentials)
		{
			api.Logger.Notification("[ServerEssentials/Admin] Root /restart was already registered before NSE command setup. NSE will leave it untouched; /nse restart remains available.");
		}
		SafeRegister("nse", "Newfies Server Essentials administration and player utilities", "spawn|msg|r|starterkit|setstarterkit|resetstarterkitusage|resetstarterkitusageall|back|home|sethome|delhome|homes|t2p|t2m|tpaccept|rtp|warp|warps|warpinfo|setwarp|delwarp|setwarpcost|setwarpcooldown|setwarpwarmup|setwarpmove|warpreload|player|report|reports|reportstatus|reportassign|reportnote|warn|warnings|unwarn|mute|unmute|muteinfo|tempban|baninfo|unban|note|history|freeze|unfreeze|goto|bring|sendback|smite|setjail|jail|unjail|jailinfo|invsee|invsnapshot|invrestore|invgive|invqty|invmove|invreplace|invremove|heal|feed|revive|ghost|seen|playtime|afk|afkinfo|online|laghere|cleanup|maintenance|shutdown|restart|restarttime|autorestart|undo", OnNse);
		SafeRegister("warn", "Warn a player", "<player> <reason>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Warn(p, g, a);
		}, "nse.moderation.warn");
		SafeRegister("warnings", "List player warnings", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Warnings(p, g, a);
		}, "nse.moderation.warn");
		SafeRegister("unwarn", "Remove a warning", "<player> <warningId>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Unwarn(p, g, a);
		}, "nse.moderation.warn");
		SafeRegister("tempban", "Temporarily ban a player", "<player> <30m|6h|3d|2w> <reason>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			TempBan(p, g, a);
		}, "nse.moderation.ban");
		SafeRegister("baninfo", "Show suite temporary-ban state", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			BanInfo(p, g, a);
		}, "nse.moderation.ban");
		SafeRegister("mute", "Mute a player", "<player> <duration|permanent> <reason>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Mute(p, g, a);
		}, "nse.moderation.mute");
		SafeRegister("unmute", "Unmute a player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Unmute(p, g, a);
		}, "nse.moderation.mute");
		SafeRegister("muteinfo", "Show mute state", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			MuteInfo(p, g, a);
		}, "nse.moderation.mute");
		SafeRegister("freeze", "Freeze a player", "<player> [duration|permanent] [reason]", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Freeze(p, g, a);
		}, "nse.player.manage");
		SafeRegister("unfreeze", "Unfreeze a player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Unfreeze(p, g, a);
		}, "nse.player.manage");
		SafeRegister("goto", "Teleport to a player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Goto(p, g, a);
		}, "nse.teleport");
		SafeRegister("bring", "Bring a player to you", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Bring(p, g, a);
		}, "nse.teleport");
		SafeRegister("tphere", "Bring a player to you", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Bring(p, g, a);
		}, "nse.teleport");
		SafeRegister("sendback", "Return a brought player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			SendBack(p, g, a);
		}, "nse.teleport");
		SafeRegister("invsee", "Read-only player inventory summary", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvSee(p, g, a);
		}, "nse.inventory.view");
		SafeRegister("invremove", "Remove one player inventory slot with audit/undo", "<player> <inventoryId> <slot> --confirm", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvRemove(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("invsnapshot", "Create an inventory snapshot", "<player> [reason]", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvSnapshot(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("invrestore", "Restore an inventory snapshot", "<player> <snapshotId> --confirm", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvRestore(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("invgive", "Give an item/block with automatic snapshot", "<player> <code> <qty>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvGive(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("invqty", "Change one stack quantity with automatic snapshot", "<player> <inventoryId> <slot> <qty>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvQty(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("invmove", "Move/swap inventory slots with automatic snapshot", "<player> <fromInv> <fromSlot> <toInv> <toSlot>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvMove(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("invreplace", "Replace one inventory slot with automatic snapshot", "<player> <inventoryId> <slot> <code> <qty>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			InvReplace(p, g, a);
		}, "nse.inventory.edit");
		SafeRegister("heal", "Heal a player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			SimpleTarget(p, g, a, "heal");
		}, "nse.player.manage");
		SafeRegister("feed", "Feed a player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			SimpleTarget(p, g, a, "feed");
		}, "nse.player.manage");
		SafeRegister("revive", "Revive a player", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			SimpleTarget(p, g, a, "revive");
		}, "nse.player.manage");
		SafeRegister("smite", "Strike a player with lightning", "<player> [reason]", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Smite(p, g, a);
		}, "nse.smite");
		SafeRegister("setjail", "Set the server jail cell to your current position", "", (IServerPlayer p, int g, CmdArgs a) =>
		{
			SetJail(p, g);
		}, "nse.jail");
		SafeRegister("jail", "Jail a player for a duration", "<player> <duration|permanent> [reason]", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Jail(p, g, a);
		}, "nse.jail");
		SafeRegister("unjail", "Release a player from jail", "<player> [reason]", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Unjail(p, g, a);
		}, "nse.jail");
		SafeRegister("jailinfo", "Show a player jail sentence", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			JailInfo(p, g, a);
		}, "nse.jail");
		SafeRegister("ghost", "Toggle your admin ghost/spectator mode", "", (IServerPlayer p, int g, CmdArgs a) =>
		{
			ReplyAction(p, g, "ghost", admin.ToggleGhost(p));
		}, "nse.player.manage");
		SafeRegister("seen", "Show when a player was last seen", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Seen(p, g, a);
		}, "nse.player.view");
		SafeRegister("playtime", "Show tracked playtime", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Playtime(p, g, a);
		}, "nse.player.view");
		SafeRegister("afkinfo", "Show player AFK/idle state", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			AfkInfo(p, g, a);
		}, "nse.player.view");
		SafeRegister("online", "List online players with ping", "", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Online(p, g);
		}, "nse.player.view");
		SafeRegister("history", "Show unified suite and BlockLogger player history", "<player>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			History(p, g, a);
		}, "nse.player.view");
		SafeRegister("note", "Manage private staff notes", "<player> add <text>|list|remove <id>", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Note(p, g, a);
		}, "nse.player.manage");
		if (reports != null)
		{
			try
			{
				IChatCommand chatCommand = api.ChatCommands.Get("report");
				if (chatCommand == null)
				{
					SafeRegister("report", "Report an issue to server staff", "<issue>", (IServerPlayer p, int g, CmdArgs a) =>
					{
						Report(p, g, a);
					}, Privilege.chat);
					reports.SetRootCommandOwnership("Newfies Server Essentials", suiteOwns: true);
					api.Logger.Notification("[ServerEssentials/Reports] Registered root /report command.");
				}
				else
				{
					reports.SetRootCommandOwnership("Another registered provider", suiteOwns: false);
					api.Logger.Notification("[ServerEssentials/Reports] Root /report already exists. NSE will not replace it; /nse report remains available.");
				}
			}
			catch (Exception ex)
			{
				reports.SetRootCommandOwnership("Unknown (inspection failed)", suiteOwns: false);
				api.Logger.Warning("[ServerEssentials/Reports] Could not inspect/register root /report: {0}: {1}", ex.GetType().Name, ex.Message);
			}
		}
		if (!hasTh3Essentials)
		{
			SafeRegister("shutdown", "Schedule a clean server shutdown", "<delay> [reason]|cancel", (IServerPlayer p, int g, CmdArgs a) =>
			{
				Shutdown(p, g, a);
			}, "nse.server");
		}
		if (!hasTh3Essentials && !isStratum && !flag)
		{
			SafeRegister("restart", "Schedule a clean shutdown for an external supervisor restart", "<delay> [reason]|cancel", (IServerPlayer p, int g, CmdArgs a) =>
			{
				Restart(p, g, a);
			}, "nse.server");
		}
		SafeRegister("nseundo", "Undo the most recent reversible suite admin action", "", (IServerPlayer p, int g, CmdArgs a) =>
		{
			ReplyAction(p, g, "undo", admin.Undo(p.PlayerName));
		}, "nse.player.manage");
		SafeRegister("laghere", "Scan nearby entities for lag investigation", "[radiusChunks]", (IServerPlayer p, int g, CmdArgs a) =>
		{
			LagHere(p, g, a);
		}, "nse.diagnostics");
		SafeRegister("maintenance", "Manage maintenance mode", "on [duration] [reason]|off|status", (IServerPlayer p, int g, CmdArgs a) =>
		{
			Maintenance(p, g, a);
		}, "nse.server");
	}

	private bool RootCommandExists(string command)
	{
		try
		{
			return api.ChatCommands.Get(command) != null;
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Could not inspect root command /{0}: {1}: {2}", command, ex.GetType().Name, ex.Message);
			return false;
		}
	}

	private void SafeRegister(string command, string description, string syntax, ServerChatCommandDelegate handler, string? requiredPrivilege = null)
	{
		try
		{
			if (api.ChatCommands.Get(command) != null)
			{
				api.Logger.Notification("[ServerEssentials/Admin] Root command /{0} is already owned by another provider. NSE skipped that alias; use /nse {0} where available.", command);
			}
			else
			{
				api.RegisterCommand(command, description, syntax, handler, requiredPrivilege);
			}
		}
		catch (Exception ex)
		{
			api.Logger.Warning("[ServerEssentials/Admin] Could not register root alias /{0}; leaving the existing provider untouched. {1}: {2}", command, ex.GetType().Name, ex.Message);
		}
	}

	private void OnNse(IServerPlayer player, int groupId, CmdArgs args)
	{
		switch ((args.PopWord() ?? "help").ToLowerInvariant())
		{
		case "spawn":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleSpawn(player, groupId, args);
			}
			break;
		case "msg":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleMsg(player, groupId, args);
			}
			break;
		case "r":
		case "reply":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleReply(player, groupId, args);
			}
			break;
		case "starterkit":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleStarterKit(player, groupId, args);
			}
			break;
		case "setstarterkit":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleSetStarterKit(player, groupId, args);
				}
			});
			break;
		case "resetstarterkitusage":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleResetStarterKitUsage(player, groupId, args);
				}
			});
			break;
		case "resetstarterkitusageall":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleResetStarterKitUsageAll(player, groupId, args);
				}
			});
			break;
		case "back":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleBack(player, groupId, args);
			}
			break;
		case "home":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleHome(player, groupId, args);
			}
			break;
		case "sethome":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleSetHome(player, groupId, args);
			}
			break;
		case "delhome":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleDeleteHome(player, groupId, args);
			}
			break;
		case "homes":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleHomes(player, groupId, args);
			}
			break;
		case "t2p":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleT2p(player, groupId, args);
			}
			break;
		case "t2m":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleT2m(player, groupId, args);
			}
			break;
		case "tpaccept":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleTpAccept(player, groupId, args);
			}
			break;
		case "rtp":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleRtp(player, groupId, args);
			}
			break;
		case "warp":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleWarp(player, groupId, args);
			}
			break;
		case "warps":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleWarps(player, groupId, args);
			}
			break;
		case "warpinfo":
			if (playerCommands == null)
			{
				Reply(player, groupId, "Player Commands feature is disabled.", success: false);
			}
			else
			{
				playerCommands.HandleWarpInfo(player, groupId, args);
			}
			break;
		case "setwarp":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleSetWarp(player, groupId, args);
				}
			});
			break;
		case "delwarp":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleDeleteWarp(player, groupId, args);
				}
			});
			break;
		case "setwarpcost":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleSetWarpCost(player, groupId, args);
				}
			});
			break;
		case "setwarpcooldown":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleSetWarpCooldown(player, groupId, args);
				}
			});
			break;
		case "setwarpwarmup":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleSetWarpWarmup(player, groupId, args);
				}
			});
			break;
		case "setwarpmove":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleSetWarpMovement(player, groupId, args);
				}
			});
			break;
		case "warpreload":
			Require(player, groupId, Privilege.controlserver, () =>
			{
				if (playerCommands == null)
				{
					Reply(player, groupId, "Player Commands feature is disabled.", success: false);
				}
				else
				{
					playerCommands.HandleReloadWarps(player, groupId, args);
				}
			});
			break;
		case "restarttime":
		{
			AutomatedRestartService automatedRestartService = getAutomatedRestarts();
			if (automatedRestartService == null)
			{
				Reply(player, groupId, "Automated restart service is unavailable.", success: false);
			}
			else
			{
				automatedRestartService.HandleRestartTime(player, groupId, args);
			}
			break;
		}
		case "autorestart":
			Require(player, groupId, "nse.server", () =>
			{
				AutoRestart(player, groupId, args);
			});
			break;
		case "player":
			Require(player, groupId, "nse.player.view", () =>
			{
				PlayerInfo(player, groupId, args);
			});
			break;
		case "report":
			Report(player, groupId, args);
			break;
		case "reports":
			Require(player, groupId, "nse.reports.manage", () =>
			{
				ReportsList(player, groupId, args);
			});
			break;
		case "reportstatus":
			Require(player, groupId, "nse.reports.manage", () =>
			{
				ReportStatus(player, groupId, args);
			});
			break;
		case "reportassign":
			Require(player, groupId, "nse.reports.manage", () =>
			{
				ReportAssign(player, groupId, args);
			});
			break;
		case "reportnote":
			Require(player, groupId, "nse.reports.manage", () =>
			{
				ReportNote(player, groupId, args);
			});
			break;
		case "warn":
			Require(player, groupId, "nse.moderation.warn", () =>
			{
				Warn(player, groupId, args);
			});
			break;
		case "warnings":
			Require(player, groupId, "nse.moderation.warn", () =>
			{
				Warnings(player, groupId, args);
			});
			break;
		case "unwarn":
			Require(player, groupId, "nse.moderation.warn", () =>
			{
				Unwarn(player, groupId, args);
			});
			break;
		case "mute":
			Require(player, groupId, "nse.moderation.mute", () =>
			{
				Mute(player, groupId, args);
			});
			break;
		case "unmute":
			Require(player, groupId, "nse.moderation.mute", () =>
			{
				Unmute(player, groupId, args);
			});
			break;
		case "muteinfo":
			Require(player, groupId, "nse.moderation.mute", () =>
			{
				MuteInfo(player, groupId, args);
			});
			break;
		case "tempban":
			Require(player, groupId, "nse.moderation.ban", () =>
			{
				TempBan(player, groupId, args);
			});
			break;
		case "baninfo":
			Require(player, groupId, "nse.moderation.ban", () =>
			{
				BanInfo(player, groupId, args);
			});
			break;
		case "unban":
			Require(player, groupId, "nse.moderation.ban", () =>
			{
				Unban(player, groupId, args);
			});
			break;
		case "note":
			Require(player, groupId, "nse.player.manage", () =>
			{
				Note(player, groupId, args);
			});
			break;
		case "history":
			Require(player, groupId, "nse.player.view", () =>
			{
				History(player, groupId, args);
			});
			break;
		case "freeze":
			Require(player, groupId, "nse.player.manage", () =>
			{
				Freeze(player, groupId, args);
			});
			break;
		case "unfreeze":
			Require(player, groupId, "nse.player.manage", () =>
			{
				Unfreeze(player, groupId, args);
			});
			break;
		case "goto":
			Require(player, groupId, "nse.teleport", () =>
			{
				Goto(player, groupId, args);
			});
			break;
		case "bring":
		case "tphere":
			Require(player, groupId, "nse.teleport", () =>
			{
				Bring(player, groupId, args);
			});
			break;
		case "sendback":
			Require(player, groupId, "nse.teleport", () =>
			{
				SendBack(player, groupId, args);
			});
			break;
		case "smite":
			Require(player, groupId, "nse.smite", () =>
			{
				Smite(player, groupId, args);
			});
			break;
		case "setjail":
			Require(player, groupId, "nse.jail", () =>
			{
				SetJail(player, groupId);
			});
			break;
		case "jail":
			Require(player, groupId, "nse.jail", () =>
			{
				Jail(player, groupId, args);
			});
			break;
		case "unjail":
			Require(player, groupId, "nse.jail", () =>
			{
				Unjail(player, groupId, args);
			});
			break;
		case "jailinfo":
			Require(player, groupId, "nse.jail", () =>
			{
				JailInfo(player, groupId, args);
			});
			break;
		case "invsee":
			Require(player, groupId, "nse.inventory.view", () =>
			{
				InvSee(player, groupId, args);
			});
			break;
		case "invsnapshot":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvSnapshot(player, groupId, args);
			});
			break;
		case "invrestore":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvRestore(player, groupId, args);
			});
			break;
		case "invgive":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvGive(player, groupId, args);
			});
			break;
		case "invqty":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvQty(player, groupId, args);
			});
			break;
		case "invmove":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvMove(player, groupId, args);
			});
			break;
		case "invreplace":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvReplace(player, groupId, args);
			});
			break;
		case "invremove":
			Require(player, groupId, "nse.inventory.edit", () =>
			{
				InvRemove(player, groupId, args);
			});
			break;
		case "heal":
			Require(player, groupId, "nse.player.manage", () =>
			{
				SimpleTarget(player, groupId, args, "heal");
			});
			break;
		case "feed":
			Require(player, groupId, "nse.player.manage", () =>
			{
				SimpleTarget(player, groupId, args, "feed");
			});
			break;
		case "revive":
			Require(player, groupId, "nse.player.manage", () =>
			{
				SimpleTarget(player, groupId, args, "revive");
			});
			break;
		case "ghost":
			Require(player, groupId, "nse.player.manage", () =>
			{
				ReplyAction(player, groupId, "ghost", admin.ToggleGhost(player));
			});
			break;
		case "seen":
			Require(player, groupId, "nse.player.view", () =>
			{
				Seen(player, groupId, args);
			});
			break;
		case "playtime":
			Require(player, groupId, "nse.player.view", () =>
			{
				Playtime(player, groupId, args);
			});
			break;
		case "afk":
			Require(player, groupId, "afk", () =>
			{
				AfkToggle(player, groupId);
			});
			break;
		case "afkinfo":
			Require(player, groupId, "nse.player.view", () =>
			{
				AfkInfo(player, groupId, args);
			});
			break;
		case "online":
			Require(player, groupId, "nse.player.view", () =>
			{
				Online(player, groupId);
			});
			break;
		case "laghere":
			Require(player, groupId, "nse.diagnostics", () =>
			{
				LagHere(player, groupId, args);
			});
			break;
		case "cleanup":
			Require(player, groupId, "nse.cleanup", () =>
			{
				Cleanup(player, groupId, args);
			});
			break;
		case "maintenance":
			Require(player, groupId, "nse.server", () =>
			{
				Maintenance(player, groupId, args);
			});
			break;
		case "shutdown":
			Require(player, groupId, "nse.server", () =>
			{
				Shutdown(player, groupId, args);
			});
			break;
		case "restart":
			Require(player, groupId, "nse.server", () =>
			{
				Restart(player, groupId, args);
			});
			break;
		case "undo":
			Require(player, groupId, "nse.player.manage", () =>
			{
				ReplyAction(player, groupId, "undo", admin.Undo(player.PlayerName));
			});
			break;
		default:
			SendHelp(player, groupId);
			break;
		}
	}

	private void Report(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopAll();
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /report <issue>", success: false);
			return;
		}
		if (reports == null)
		{
			Reply(p, g, "Reports feature is disabled.", success: false);
			return;
		}
		(bool, string, PlayerReportRecord) tuple = reports.CreateReport(p, text);
		Reply(p, g, tuple.Item2, tuple.Item1);
	}

	private void ReportsList(IServerPlayer p, int g, CmdArgs a)
	{
		string text = (a.PopWord() ?? "open").ToLowerInvariant();
		bool flag;
		switch (text)
		{
		case "open":
		case "investigating":
		case "resolved":
		case "all":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			Reply(p, g, "Usage: /nse reports [open|investigating|resolved|all]", success: false);
			return;
		}
		if (reports == null)
		{
			Reply(p, g, "Reports feature is disabled.", success: false);
			return;
		}
		PlayerReportSnapshot snapshot = reports.GetSnapshot(text, 25);
		Reply(p, g, $"Reports: open={snapshot.OpenCount}, investigating={snapshot.InvestigatingCount}, resolved={snapshot.ResolvedCount}, total={snapshot.TotalCount}.", success: true);
		if (snapshot.Reports.Count == 0)
		{
			Reply(p, g, "No " + text + " reports.", success: true);
			return;
		}
		foreach (PlayerReportRecord report in snapshot.Reports)
		{
			string value = (string.IsNullOrWhiteSpace(report.AssignedStaff) ? "unassigned" : report.AssignedStaff);
			Reply(p, g, $"#{report.Id} [{report.Status}] {report.ReporterName} | {report.Reason} | {value} | {DateTimeOffset.FromUnixTimeMilliseconds(report.CreatedUnixMilliseconds):u}", success: true);
		}
	}

	private void ReportStatus(IServerPlayer p, int g, CmdArgs a)
	{
		long valueOrDefault = a.PopLong(0L).GetValueOrDefault();
		string text = a.PopWord() ?? "";
		if (valueOrDefault <= 0 || string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /nse reportstatus <id> <open|investigating|resolved> [note]", success: false);
			return;
		}
		if (reports == null)
		{
			Reply(p, g, "Reports feature is disabled.", success: false);
			return;
		}
		string text2 = reports.SetStatus(valueOrDefault, text, p.PlayerName, a.PopAll());
		Reply(p, g, text2, ReportMutationSucceeded(text2));
	}

	private void ReportAssign(IServerPlayer p, int g, CmdArgs a)
	{
		long valueOrDefault = a.PopLong(0L).GetValueOrDefault();
		string text = a.PopAll().Trim();
		if (valueOrDefault <= 0 || string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /nse reportassign <id> <me|staff name|clear>", success: false);
			return;
		}
		if (text.Equals("me", StringComparison.OrdinalIgnoreCase))
		{
			text = p.PlayerName;
		}
		else if (text.Equals("clear", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
		{
			text = "";
		}
		if (reports == null)
		{
			Reply(p, g, "Reports feature is disabled.", success: false);
			return;
		}
		string text2 = reports.Assign(valueOrDefault, text, p.PlayerName);
		Reply(p, g, text2, ReportMutationSucceeded(text2));
	}

	private void ReportNote(IServerPlayer p, int g, CmdArgs a)
	{
		long valueOrDefault = a.PopLong(0L).GetValueOrDefault();
		string text = a.PopAll();
		if (valueOrDefault <= 0 || string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /nse reportnote <id> <private staff note>", success: false);
			return;
		}
		if (reports == null)
		{
			Reply(p, g, "Reports feature is disabled.", success: false);
			return;
		}
		string text2 = reports.AddNote(valueOrDefault, p.PlayerName, text);
		Reply(p, g, text2, ReportMutationSucceeded(text2));
	}

	private static bool ReportMutationSucceeded(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return false;
		}
		return !message.Contains("was not found", StringComparison.OrdinalIgnoreCase) && !message.StartsWith("Status must", StringComparison.OrdinalIgnoreCase) && !message.StartsWith("Note text is required", StringComparison.OrdinalIgnoreCase) && !message.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
	}

	private void PlayerInfo(IServerPlayer player, int groupId, CmdArgs args)
	{
		string playerOrUid = args.PopWord() ?? player.PlayerName;
		AdminPlayerSummary player2 = admin.GetPlayer(playerOrUid);
		if (player2 == null)
		{
			Reply(player, groupId, "Player not found.", success: false);
			return;
		}
		Reply(player, groupId, $"{player2.Name} | {(player2.Online ? "Online" : "Offline")} | UID={player2.PlayerUid} | Role={player2.Role} | Mode={player2.GameMode} | Ping={player2.PingMilliseconds}ms | Pos={player2.Position} | Health={player2.Health:0.#}/{player2.MaxHealth:0.#} | Warnings={player2.ActiveWarnings} | Muted={player2.Muted} | Banned={player2.Banned} | Frozen={player2.Frozen} | Playtime={FormatSeconds(player2.TotalPlaySeconds)}", success: true);
	}

	private void Warn(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		ReplyAction(p, g, "warn", admin.Warn(p.PlayerName, playerOrUid, a.PopAll()));
	}

	private void Warnings(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		List<AdminWarningRecord> warnings = admin.GetWarnings(playerOrUid);
		if (warnings.Count == 0)
		{
			Reply(p, g, "No warnings found.", success: true);
			return;
		}
		foreach (AdminWarningRecord item in warnings.Take(20))
		{
			Reply(p, g, $"#{item.Id} {(item.Removed ? "REMOVED" : "ACTIVE")} {DateTimeOffset.FromUnixTimeMilliseconds(item.UnixMilliseconds):u} by {item.IssuedBy}: {item.Reason}", success: true);
		}
	}

	private void Unwarn(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		long valueOrDefault = a.PopLong(0L).GetValueOrDefault();
		ReplyAction(p, g, "unwarn", admin.RemoveWarning(p.PlayerName, playerOrUid, valueOrDefault));
	}

	private void TempBan(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string duration = a.PopWord() ?? "";
		ReplyAction(p, g, "tempban", admin.TempBan(p.PlayerName, playerOrUid, duration, a.PopAll()));
	}

	private void Unban(IServerPlayer p, int g, CmdArgs a)
	{
		ReplyAction(p, g, "unban", admin.Unban(p.PlayerName, a.PopWord() ?? ""));
	}

	private void BanInfo(IServerPlayer p, int g, CmdArgs a)
	{
		AdminPlayerSummary player = admin.GetPlayer(a.PopWord() ?? "");
		if (player == null)
		{
			Reply(p, g, "Player not found.", success: false);
			return;
		}
		Reply(p, g, player.Banned ? $"{player.Name} is temporarily banned until {DateTimeOffset.FromUnixTimeMilliseconds(player.BanUntilUnixMilliseconds):u}." : (player.Name + " has no active suite temporary ban."), success: true);
	}

	private void Mute(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string duration = a.PopWord() ?? "";
		ReplyAction(p, g, "mute", admin.Mute(p.PlayerName, playerOrUid, duration, a.PopAll()));
	}

	private void Unmute(IServerPlayer p, int g, CmdArgs a)
	{
		ReplyAction(p, g, "unmute", admin.Unmute(p.PlayerName, a.PopWord() ?? ""));
	}

	private void MuteInfo(IServerPlayer p, int g, CmdArgs a)
	{
		AdminPlayerSummary player = admin.GetPlayer(a.PopWord() ?? "");
		if (player == null)
		{
			Reply(p, g, "Player not found.", success: false);
			return;
		}
		Reply(p, g, player.Muted ? (player.Name + " is muted" + ((player.MuteUntilUnixMilliseconds > 0) ? $" until {DateTimeOffset.FromUnixTimeMilliseconds(player.MuteUntilUnixMilliseconds):u}" : " permanently") + ".") : (player.Name + " is not muted."), success: true);
	}

	private void Note(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string text = (a.PopWord() ?? "list").ToLowerInvariant();
		if (text == "add")
		{
			ReplyAction(p, g, "note", admin.AddNote(p.PlayerName, playerOrUid, a.PopAll()));
			return;
		}
		if (text == "remove")
		{
			ReplyAction(p, g, "note-remove", admin.RemoveNote(p.PlayerName, playerOrUid, a.PopLong(0L).GetValueOrDefault()));
			return;
		}
		List<AdminNoteRecord> notes = admin.GetNotes(playerOrUid);
		if (notes.Count == 0)
		{
			Reply(p, g, "No staff notes.", success: true);
			return;
		}
		foreach (AdminNoteRecord item in notes.Take(20))
		{
			Reply(p, g, $"#{item.Id} {DateTimeOffset.FromUnixTimeMilliseconds(item.UnixMilliseconds):u} {item.Author}: {item.Text}", success: true);
		}
	}

	private void History(IServerPlayer p, int g, CmdArgs a)
	{
		string target = a.PopWord() ?? p.PlayerName;
		HistoryAsync(p, g, target);
	}

	private async Task HistoryAsync(IServerPlayer p, int g, string target)
	{
		try
		{
			List<AdminAuditRecord> adminHistory = admin.GetHistory(target, 50);
			List<LogEventRecord> blockHistory = await admin.GetBlockHistoryAsync(target, 50).ConfigureAwait(continueOnCapturedContext: false);
			List<(DateTime Time, string Text)> rows = new List<(DateTime, string)>();
			rows.AddRange(adminHistory.Select((AdminAuditRecord x) => (UtcDateTime: DateTimeOffset.FromUnixTimeMilliseconds(x.UnixMilliseconds).UtcDateTime, $"ADMIN {x.Action} by {x.Actor}: {x.Detail}")));
			rows.AddRange(blockHistory.Select((LogEventRecord x) => (x.TimestampUtc.ToUniversalTime(), $"BLOCKLOG {x.Action}: {x.BlockCode} @ {x.X},{x.Y},{x.Z}")));
			List<(DateTime Time, string Text)> ordered = rows.OrderByDescending(((DateTime Time, string Text) x) => x.Time).Take(30).ToList();
			api.Event.EnqueueMainThreadTask(() =>
			{
				if (ordered.Count == 0)
				{
					Reply(p, g, "No suite or BlockLogger history found.", success: true);
					return;
				}
				foreach (var item in ordered)
				{
					Reply(p, g, $"{item.Time:u} {item.Text}", success: true);
				}
			}, "nse-admin-history");
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			Exception ex3 = ex2;
			api.Event.EnqueueMainThreadTask(() =>
			{
				Reply(p, g, "History query failed: " + ex3.Message, success: false);
			}, "nse-admin-history-error");
		}
	}

	private void Freeze(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string duration = a.PopWord() ?? "permanent";
		ReplyAction(p, g, "freeze", admin.Freeze(p.PlayerName, playerOrUid, duration, a.PopAll()));
	}

	private void Unfreeze(IServerPlayer p, int g, CmdArgs a)
	{
		ReplyAction(p, g, "unfreeze", admin.Unfreeze(p.PlayerName, a.PopWord() ?? ""));
	}

	private void Goto(IServerPlayer p, int g, CmdArgs a)
	{
		ReplyAction(p, g, "goto", admin.Goto(p, a.PopWord() ?? ""));
	}

	private void Bring(IServerPlayer p, int g, CmdArgs a)
	{
		ReplyAction(p, g, "bring", admin.Bring(p, a.PopWord() ?? ""));
	}

	private void SendBack(IServerPlayer p, int g, CmdArgs a)
	{
		ReplyAction(p, g, "sendback", admin.SendBack(p.PlayerName, a.PopWord() ?? ""));
	}

	private void Smite(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /smite <player> [reason]", success: false);
			return;
		}
		string text2 = admin.Smite(p.PlayerName, text, a.PopAll());
		bool success = AdminService.IsSuccessfulActionResult("smite", text2);
		Reply(p, g, text2, success);
	}

	private void SetJail(IServerPlayer p, int g)
	{
		string message = admin.SetJailPosition(p);
		ReplyAction(p, g, "setjail", message);
	}

	private void Jail(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		string text2 = a.PopWord() ?? "";
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			Reply(p, g, "Usage: /jail <player> <10m|2h|3d|permanent> [reason]", success: false);
			return;
		}
		string message = admin.Jail(p.PlayerName, text, text2, a.PopAll());
		ReplyAction(p, g, "jail", message);
	}

	private void Unjail(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /unjail <player> [reason]", success: false);
			return;
		}
		string message = admin.Unjail(p.PlayerName, text, a.PopAll());
		ReplyAction(p, g, "unjail", message);
	}

	private void JailInfo(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			Reply(p, g, "Usage: /jailinfo <player>", success: false);
			return;
		}
		string text2 = admin.JailInfo(text);
		bool success = !text2.Contains("not jailed", StringComparison.OrdinalIgnoreCase) && !text2.Contains("unavailable", StringComparison.OrdinalIgnoreCase);
		Reply(p, g, text2, success);
	}

	private void InvSee(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		List<AdminInventorySlotView> inventory = admin.GetInventory(text);
		List<AdminInventorySlotView> list = inventory.Where((AdminInventorySlotView x) => !x.Empty).ToList();
		if (inventory.Count == 0)
		{
			Reply(p, g, "Player must be online or inventory is unavailable.", success: false);
			return;
		}
		Reply(p, g, $"Inventory for {text}: {list.Count} occupied / {inventory.Count} total slots.", success: true);
		foreach (IGrouping<string, AdminInventorySlotView> item in from x in list
			group x by x.Inventory)
		{
			Reply(p, g, item.Key + ": " + string.Join(", ", from x in item.Take(20)
				select $"[{x.Slot}] {x.StackSize}x {x.Code}") + ((item.Count() > 20) ? " ..." : ""), success: true);
		}
	}

	private void InvRemove(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string inventoryId = a.PopWord() ?? "";
		int slotIndex = a.PopInt(-1) ?? (-1);
		string a2 = a.PopWord() ?? "";
		if (!string.Equals(a2, "--confirm", StringComparison.OrdinalIgnoreCase))
		{
			Reply(p, g, "Usage: /invremove <player> <inventoryId> <slot> --confirm", success: false);
		}
		else
		{
			ReplyAction(p, g, "inventory-remove", admin.RemoveInventorySlot(p.PlayerName, playerOrUid, inventoryId, slotIndex));
		}
	}

	private void InvSnapshot(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string text = a.PopAll();
		ReplyAction(p, g, "inventory-snapshot", admin.CreateInventorySnapshot(p.PlayerName, playerOrUid, string.IsNullOrWhiteSpace(text) ? "Manual in-game snapshot" : text));
	}

	private void InvRestore(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		long valueOrDefault = a.PopLong(0L).GetValueOrDefault();
		string a2 = a.PopWord() ?? "";
		if (!string.Equals(a2, "--confirm", StringComparison.OrdinalIgnoreCase))
		{
			Reply(p, g, "Usage: /invrestore <player> <snapshotId> --confirm", success: false);
		}
		else
		{
			ReplyAction(p, g, "inventory-restore", admin.RestoreInventorySnapshot(p.PlayerName, playerOrUid, valueOrDefault));
		}
	}

	private void InvGive(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string code = a.PopWord() ?? "";
		int quantity = a.PopInt(1) ?? 1;
		ReplyAction(p, g, "inventory-give", admin.GiveInventoryItem(p.PlayerName, playerOrUid, code, quantity));
	}

	private void InvQty(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string inventoryId = a.PopWord() ?? "";
		int slotIndex = a.PopInt(-1) ?? (-1);
		int quantity = a.PopInt(-1) ?? (-1);
		ReplyAction(p, g, "inventory-quantity", admin.SetInventoryQuantity(p.PlayerName, playerOrUid, inventoryId, slotIndex, quantity));
	}

	private void InvMove(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string fromInventoryId = a.PopWord() ?? "";
		int fromSlotIndex = a.PopInt(-1) ?? (-1);
		string toInventoryId = a.PopWord() ?? "";
		int toSlotIndex = a.PopInt(-1) ?? (-1);
		ReplyAction(p, g, "inventory-move", admin.MoveInventorySlot(p.PlayerName, playerOrUid, fromInventoryId, fromSlotIndex, toInventoryId, toSlotIndex));
	}

	private void InvReplace(IServerPlayer p, int g, CmdArgs a)
	{
		string playerOrUid = a.PopWord() ?? "";
		string inventoryId = a.PopWord() ?? "";
		int slotIndex = a.PopInt(-1) ?? (-1);
		string code = a.PopWord() ?? "";
		int quantity = a.PopInt(1) ?? 1;
		ReplyAction(p, g, "inventory-replace", admin.ReplaceInventorySlot(p.PlayerName, playerOrUid, inventoryId, slotIndex, code, quantity));
	}

	private void SimpleTarget(IServerPlayer p, int g, CmdArgs a, string op)
	{
		string playerOrUid = a.PopWord() ?? "";
		if (1 == 0)
		{
		}
		string text;
		if (op == "heal")
		{
			text = admin.Heal(p.PlayerName, playerOrUid);
		}
		else
		{
			text = ((!(op == "feed")) ? admin.Revive(p.PlayerName, playerOrUid) : admin.Feed(p.PlayerName, playerOrUid));
		}
		if (1 == 0)
		{
		}
		string message = text;
		ReplyAction(p, g, op, message);
	}

	private void Seen(IServerPlayer p, int g, CmdArgs a)
	{
		AdminPlayerSummary player = admin.GetPlayer(a.PopWord() ?? "");
		if (player == null)
		{
			Reply(p, g, "Player not found.", success: false);
			return;
		}
		Reply(p, g, player.Online ? (player.Name + " is online now.") : $"{player.Name} was last seen {DateTimeOffset.FromUnixTimeMilliseconds(player.LastSeenUnixMilliseconds):u}.", success: true);
	}

	private void Playtime(IServerPlayer p, int g, CmdArgs a)
	{
		AdminPlayerSummary player = admin.GetPlayer(a.PopWord() ?? "");
		if (player == null)
		{
			Reply(p, g, "Player not found.", success: false);
			return;
		}
		Reply(p, g, $"{player.Name}: tracked playtime {FormatSeconds(player.TotalPlaySeconds)}; current session {FormatSeconds(player.SessionSeconds)}.", success: true);
	}

	private void AfkToggle(IServerPlayer p, int g)
	{
		NewfiesAfkModSystem current = NewfiesAfkModSystem.Current;
		if (current == null)
		{
			Reply(p, g, "AFK feature is disabled or unavailable. Enable the afk feature gate and restart the server.", success: false);
			return;
		}
		(bool, string) tuple = current.ToggleForPlayer(p);
		Reply(p, g, tuple.Item2, tuple.Item1);
	}

	private void AfkInfo(IServerPlayer p, int g, CmdArgs a)
	{
		AdminPlayerSummary player = admin.GetPlayer(a.PopWord() ?? "");
		if (player == null)
		{
			Reply(p, g, "Player not found.", success: false);
			return;
		}
		if (!player.Online)
		{
			Reply(p, g, player.Name + " is offline.", success: true);
			return;
		}
		NewfiesAfkModSystem? current = NewfiesAfkModSystem.Current;
		string value;
		if (current != null && current.IsAfk(player.PlayerUid))
		{
			value = "AFK (/afk active)";
		}
		else
		{
			value = (player.Afk ? "idle/AFK" : "active");
		}
		Reply(p, g, $"{player.Name}: {value} ({FormatSeconds(player.IdleSeconds)} since movement/chat activity).", success: true);
	}

	private void Online(IServerPlayer p, int g)
	{
		List<AdminPlayerSummary> list = (from x in admin.GetPlayers()
			where x.Online
			select x).ToList();
		Reply(p, g, $"Online ({list.Count}): {string.Join(", ", list.Select((AdminPlayerSummary x) => $"{x.Name}({x.PingMilliseconds}ms)"))}", success: true);
	}

	private void LagHere(IServerPlayer p, int g, CmdArgs a)
	{
		int num = Math.Clamp(a.PopInt(3) ?? 3, 1, 12);
		LagHereReport lagHereReport = admin.ScanLagHere(p, num);
		Reply(p, g, $"LagHere {num} chunks: entities={lagHereReport.Entities}, droppedItems={lagHereReport.DroppedItems}, players={lagHereReport.Players}, serverChunks={lagHereReport.LoadedChunks}, delay={lagHereReport.MainThreadDelayMs:0.#}ms, CPU={lagHereReport.CpuPercent:0.#}%, BLQueue={lagHereReport.BlockLoggerQueue}. Top: {string.Join(", ", lagHereReport.TopEntityTypes.Select((KeyValuePair<string, int> x) => $"{x.Key}={x.Value}"))}", success: true);
	}

	private void Cleanup(IServerPlayer p, int g, CmdArgs a)
	{
		string text = (a.PopWord() ?? "scan").ToLowerInvariant();
		if (text == "scan")
		{
			CleanupReport cleanupReport = admin.ScanCleanup();
			Reply(p, g, $"Cleanup scan: loaded={cleanupReport.LoadedEntities}, creatures={cleanupReport.Creatures}, inanimate={cleanupReport.InanimateEntities}, dead/despawning={cleanupReport.DeadOrDespawning}, droppedItems={cleanupReport.DroppedItems}. Use /nse cleanup drops --confirm to remove only dropped items.", success: true);
		}
		else if (text == "drops")
		{
			bool flag = string.Equals(a.PopWord(), "--confirm", StringComparison.OrdinalIgnoreCase);
			CleanupReport cleanupReport2 = admin.CleanupDrops(!flag);
			admin.Audit(p.PlayerName, flag ? "cleanup-drops" : "cleanup-drops-scan", "", "server", flag ? $"Removed {cleanupReport2.Removed} dropped item entities" : $"Dry run found {cleanupReport2.DroppedItems} dropped item entities", "ingame");
			Reply(p, g, flag ? $"Removed {cleanupReport2.Removed} dropped item entities." : $"Dry run: would remove {cleanupReport2.DroppedItems} dropped item entities. Re-run with --confirm.", success: true);
		}
		else
		{
			Reply(p, g, "Usage: /nse cleanup scan | /nse cleanup drops --confirm", success: false);
		}
	}

	private void AutoRestart(IServerPlayer p, int g, CmdArgs a)
	{
		AutomatedRestartService automatedRestartService = getAutomatedRestarts();
		if (automatedRestartService == null)
		{
			Reply(p, g, "Automated restart service is unavailable.", success: false);
			return;
		}
		string text = (a.PopWord() ?? "status").Trim().ToLowerInvariant();
		if (text == "status")
		{
			Reply(p, g, automatedRestartService.StatusText(), success: true);
		}
		else if (text == "on" || text == "enable")
		{
			Reply(p, g, automatedRestartService.SetEnabled(enabled: true), success: true);
		}
		else if (text == "off" || text == "disable")
		{
			Reply(p, g, automatedRestartService.SetEnabled(enabled: false), success: true);
		}
		else if (text == "reload")
		{
			Reply(p, g, automatedRestartService.Reload(), success: true);
		}
		else if (text == "skip")
		{
			Reply(p, g, automatedRestartService.SkipNext(p.PlayerName), success: true);
		}
		else
		{
			Reply(p, g, "Usage: /nse autorestart status|on|off|reload|skip", success: false);
		}
	}

	private void Maintenance(IServerPlayer p, int g, CmdArgs a)
	{
		switch ((a.PopWord() ?? "status").ToLowerInvariant())
		{
		case "status":
		{
			(bool, string, long) maintenance = admin.GetMaintenance();
			Reply(p, g, $"Maintenance={maintenance.Item1}, reason={maintenance.Item2}{((maintenance.Item3 > 0) ? $", until={DateTimeOffset.FromUnixTimeMilliseconds(maintenance.Item3):u}" : "")}", success: true);
			break;
		}
		case "off":
			ReplyAction(p, g, "maintenance", admin.SetMaintenance(p.PlayerName, enabled: false, "", 0L));
			break;
		case "on":
		{
			string text = a.PopWord() ?? "";
			long milliseconds = 0L;
			string reason;
			if (AdminService.TryParseDuration(text, out milliseconds, out var permanent) && !permanent)
			{
				reason = a.PopAll();
			}
			else
			{
				reason = (text + " " + a.PopAll()).Trim();
				milliseconds = 0L;
			}
			ReplyAction(p, g, "maintenance", admin.SetMaintenance(p.PlayerName, enabled: true, reason, milliseconds));
			break;
		}
		default:
			Reply(p, g, "Usage: /nse maintenance on [duration] [reason] | off | status", success: false);
			break;
		}
	}

	private void Shutdown(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		long milliseconds;
		bool permanent;
		if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
		{
			ReplyAction(p, g, "shutdown-cancel", admin.CancelShutdown(p.PlayerName));
		}
		else if (!AdminService.TryParseDuration(text, out milliseconds, out permanent) | permanent)
		{
			Reply(p, g, "Usage: /nse shutdown <10m|30s|...> [reason] | cancel", success: false);
		}
		else
		{
			ReplyAction(p, g, "shutdown", admin.ScheduleShutdown(p.PlayerName, milliseconds, a.PopAll()));
		}
	}

	private void Restart(IServerPlayer p, int g, CmdArgs a)
	{
		string text = a.PopWord() ?? "";
		long milliseconds;
		bool permanent;
		if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
		{
			ReplyAction(p, g, "shutdown-cancel", admin.CancelShutdown(p.PlayerName));
		}
		else if (!AdminService.TryParseDuration(text, out milliseconds, out permanent) | permanent)
		{
			Reply(p, g, "Usage: /nse restart <10m|30s|...> [reason] | cancel", success: false);
		}
		else
		{
			ReplyAction(p, g, "restart", admin.ScheduleRestart(p.PlayerName, milliseconds, a.PopAll()));
		}
	}

	private void Require(IServerPlayer p, int g, string privilege, Action action)
	{
		if (p.HasPrivilege(privilege) || p.HasPrivilege(Privilege.root) || p.HasPrivilege(Privilege.controlserver))
		{
			action();
		}
		else
		{
			Reply(p, g, "Missing privilege: " + privilege, success: false);
		}
	}

	private void SendHelp(IServerPlayer p, int g)
	{
		Reply(p, g, "/nse spawn | /nse msg <player> <message> | /nse r <message> | /nse starterkit | /nse back | /nse home [name] | /nse sethome [name] | /nse delhome [name] | /nse homes | /nse t2p <player> | /nse t2m <player> | /nse tpaccept | /nse rtp | /nse warp <name> | /nse warps | /nse warpinfo <name> | report <issue> | reports [open|investigating|resolved|all] | player <name> | warn/warnings/unwarn | mute/unmute | tempban/unban | freeze/unfreeze | goto/bring/sendback | smite <player> [reason] | setjail | jail <player> <duration> [reason] | unjail <player> | jailinfo <player> | ghost | invsee/... | seen/playtime/afk/afkinfo/online | laghere | cleanup | maintenance | shutdown/restart | restarttime | autorestart status|on|off|reload|skip | undo", success: true);
	}

	private static string FormatSeconds(long seconds)
	{
		TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Max(0L, seconds));
		string result;
		if (timeSpan.TotalDays >= 1.0)
		{
			result = $"{timeSpan.TotalDays:0.#}d";
		}
		else
		{
			result = ((timeSpan.TotalHours >= 1.0) ? $"{timeSpan.TotalHours:0.#}h" : $"{timeSpan.TotalMinutes:0.#}m");
		}
		return result;
	}

	private static void ReplyAction(IServerPlayer p, int g, string action, string message)
	{
		bool flag = AdminService.IsSuccessfulActionResult(action, message);
		string text = (flag ? "Success: " : "Failed: ");
		Reply(p, g, text + message, flag);
	}

	private static void Reply(IServerPlayer p, int g, string msg, bool success)
	{
		p.SendMessage(g, msg, (!success) ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}
}
