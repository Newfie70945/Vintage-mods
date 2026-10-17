using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

internal sealed class WebMapCommands
{
	private readonly ICoreServerAPI api;

	private readonly WebMapBridgeService webMap;

	public WebMapCommands(ICoreServerAPI api, WebMapBridgeService webMap)
	{
		this.api = api;
		this.webMap = webMap;
	}

	public void Register()
	{
		if (api.ChatCommands.Get("webmap") == null)
		{
			api.RegisterCommand("webmap", "Add or remove personal waypoints from the public Web Map", "waypoint add <major|minor> [icon:<type>] <name> | waypoint remove <name> | waypoint list | live <on|off|status> | help", OnCommand, Privilege.chat);
			api.Logger.Notification("[ServerEssentials/WebMap] Registered player /webmap waypoint commands.");
		}
		else
		{
			api.Logger.Warning("[ServerEssentials/WebMap] /webmap is already registered by another mod. Player waypoint commands were not registered to avoid replacing it.");
		}
	}

	private void OnCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = (args.PopWord() ?? "help").Trim().ToLowerInvariant();
		if ((text == "help" || text == "?") ? true : false)
		{
			SendHelp(player, groupId);
			return;
		}
		if (text == "live")
		{
			string text2 = (args.PopWord() ?? "status").Trim().ToLowerInvariant();
			if ((text2 == "off" || text2 == "on") ? true : false)
			{
				try
				{
					Reply(player, groupId, webMap.SetPlayerLiveMapVisibility(player, text2 == "on"), success: true);
					return;
				}
				catch (InvalidOperationException ex)
				{
					Reply(player, groupId, ex.Message, success: false);
					return;
				}
			}
			if (text2 == "status")
			{
				Reply(player, groupId, webMap.IsPlayerHiddenFromLiveMap(player) ? "You are currently hidden from the Web Map live-player layer." : "You are currently visible on the Web Map live-player layer when it is enabled.", success: true);
			}
			else
			{
				Reply(player, groupId, "Usage: /webmap live <on|off|status>", success: false);
			}
			return;
		}
		if (text != "waypoint" && text != "wp")
		{
			SendHelp(player, groupId);
			return;
		}
		switch ((args.PopWord() ?? "help").Trim().ToLowerInvariant())
		{
		case "add":
		{
			string text5 = (args.PopWord() ?? "").Trim().ToLowerInvariant();
			if ((!(text5 == "major") && !(text5 == "minor")) || 1 == 0)
			{
				Reply(player, groupId, "Usage: /webmap waypoint add <major|minor> <name>", success: false);
				break;
			}
			string text6 = args.PopAll().Trim();
			if (text6.Length == 0)
			{
				Reply(player, groupId, "Waypoint name is required. Example: /webmap waypoint add minor Newfies base", success: false);
				break;
			}
			string icon = "pin";
			string text7 = text6;
			int num = text6.IndexOf(' ');
			string text8 = ((num > 0) ? text6.Substring(0, num) : text6);
			if (text8.StartsWith("icon:", StringComparison.OrdinalIgnoreCase))
			{
				icon = WebMapWaypointStore.NormalizeIcon(text8.Substring(5));
				text7 = ((num > 0) ? text6.Substring(num + 1).Trim() : "");
				if (text7.Length == 0)
				{
					Reply(player, groupId, "Waypoint name is required after the icon. Example: /webmap waypoint add major icon:base Newfies Base", success: false);
					break;
				}
			}
			string text9 = webMap.AddPlayerWaypoint(player, text5, icon, text7);
			Reply(player, groupId, text9, text9.StartsWith("Added ", StringComparison.OrdinalIgnoreCase));
			break;
		}
		case "remove":
		case "delete":
		case "del":
		{
			string text3 = args.PopAll().Trim();
			if (text3.Length == 0)
			{
				Reply(player, groupId, "Usage: /webmap waypoint remove <name>", success: false);
				break;
			}
			string text4 = webMap.RemovePlayerWaypoint(player, text3);
			Reply(player, groupId, text4, text4.StartsWith("Removed ", StringComparison.OrdinalIgnoreCase));
			break;
		}
		case "list":
		case "count":
		{
			int playerWaypointCount = webMap.GetPlayerWaypointCount(player, "major");
			int playerWaypointCount2 = webMap.GetPlayerWaypointCount(player, "minor");
			WebMapConfig config = webMap.Config;
			Reply(player, groupId, $"Web Map waypoints: major {playerWaypointCount}/{config.PlayerMajorWaypointLimit}, minor {playerWaypointCount2}/{config.PlayerMinorWaypointLimit}. Major markers stay visible farther out.", success: true);
			break;
		}
		default:
			SendHelp(player, groupId);
			break;
		}
	}

	private void SendHelp(IServerPlayer player, int groupId)
	{
		int playerWaypointCount = webMap.GetPlayerWaypointCount(player, "major");
		int playerWaypointCount2 = webMap.GetPlayerWaypointCount(player, "minor");
		WebMapConfig config = webMap.Config;
		Reply(player, groupId, "Web Map waypoint commands:", success: true);
		Reply(player, groupId, "/webmap waypoint add major [icon:<type>] <name> - place a major marker at your current position", success: true);
		Reply(player, groupId, "/webmap waypoint add minor [icon:<type>] <name> - place a minor marker at your current position", success: true);
		Reply(player, groupId, "Optional icons: icon:pin, icon:base, icon:settlement, icon:shop, icon:mine, icon:resource, icon:danger, icon:landmark, icon:road, icon:farm", success: true);
		Reply(player, groupId, "/webmap waypoint remove <name> - remove one of your own markers", success: true);
		Reply(player, groupId, "/webmap live off|on|status - hide/show yourself on the live player layer", success: true);
		Reply(player, groupId, $"Waypoint usage: major {playerWaypointCount}/{config.PlayerMajorWaypointLimit}, minor {playerWaypointCount2}/{config.PlayerMinorWaypointLimit}.", success: true);
	}

	private static void Reply(IServerPlayer player, int groupId, string text, bool success)
	{
		player.SendMessage(groupId, text, (!success) ? EnumChatType.CommandError : EnumChatType.CommandSuccess);
	}
}
