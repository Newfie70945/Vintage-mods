using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.WebMap;

/// <summary>Player-facing commands for publishing/removing personal Web Map waypoints.</summary>
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
#pragma warning disable CS0618
        if (api.ChatCommands.Get("webmap") == null)
        {
            api.RegisterCommand(
                "webmap",
                "Add or remove personal waypoints from the public Web Map",
                "waypoint add <major|minor> [icon:<type>] <name> | waypoint remove <name> | waypoint list | live <on|off|status> | help",
                OnCommand,
                Privilege.chat);
            api.Logger.Notification("[ServerEssentials/WebMap] Registered player /webmap waypoint commands.");
        }
        else
        {
            api.Logger.Warning("[ServerEssentials/WebMap] /webmap is already registered by another mod. Player waypoint commands were not registered to avoid replacing it.");
        }
#pragma warning restore CS0618
    }

    private void OnCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        string root = (args.PopWord() ?? "help").Trim().ToLowerInvariant();
        if (root is "help" or "?")
        {
            SendHelp(player, groupId);
            return;
        }
        if (root == "live")
        {
            string liveOp = (args.PopWord() ?? "status").Trim().ToLowerInvariant();
            if (liveOp is "off" or "on")
            {
                try { Reply(player, groupId, webMap.SetPlayerLiveMapVisibility(player, liveOp == "on"), true); }
                catch (InvalidOperationException ex) { Reply(player, groupId, ex.Message, false); }
            }
            else if (liveOp == "status") Reply(player, groupId, webMap.IsPlayerHiddenFromLiveMap(player) ? "You are currently hidden from the Web Map live-player layer." : "You are currently visible on the Web Map live-player layer when it is enabled.", true);
            else Reply(player, groupId, "Usage: /webmap live <on|off|status>", false);
            return;
        }
        if (root != "waypoint" && root != "wp")
        {
            SendHelp(player, groupId);
            return;
        }

        string op = (args.PopWord() ?? "help").Trim().ToLowerInvariant();
        switch (op)
        {
            case "add":
            {
                string type = (args.PopWord() ?? "").Trim().ToLowerInvariant();
                if (type is not ("major" or "minor"))
                {
                    Reply(player, groupId, "Usage: /webmap waypoint add <major|minor> <name>", false);
                    return;
                }
                string remainder = args.PopAll().Trim();
                if (remainder.Length == 0)
                {
                    Reply(player, groupId, "Waypoint name is required. Example: /webmap waypoint add minor Newfies base", false);
                    return;
                }
                string icon = "pin";
                string name = remainder;
                int space = remainder.IndexOf(' ');
                string first = space > 0 ? remainder[..space] : remainder;
                if (first.StartsWith("icon:", StringComparison.OrdinalIgnoreCase))
                {
                    icon = WebMapWaypointStore.NormalizeIcon(first[5..]);
                    name = space > 0 ? remainder[(space + 1)..].Trim() : "";
                    if (name.Length == 0)
                    {
                        Reply(player, groupId, "Waypoint name is required after the icon. Example: /webmap waypoint add major icon:base Newfies Base", false);
                        return;
                    }
                }
                string result = webMap.AddPlayerWaypoint(player, type, icon, name);
                Reply(player, groupId, result, result.StartsWith("Added ", StringComparison.OrdinalIgnoreCase));
                return;
            }
            case "remove":
            case "delete":
            case "del":
            {
                string target = args.PopAll().Trim();
                if (target.Length == 0)
                {
                    Reply(player, groupId, "Usage: /webmap waypoint remove <name>", false);
                    return;
                }
                string result = webMap.RemovePlayerWaypoint(player, target);
                Reply(player, groupId, result, result.StartsWith("Removed ", StringComparison.OrdinalIgnoreCase));
                return;
            }
            case "list":
            case "count":
            {
                int major = webMap.GetPlayerWaypointCount(player, "major");
                int minor = webMap.GetPlayerWaypointCount(player, "minor");
                var cfg = webMap.Config;
                Reply(player, groupId, $"Web Map waypoints: major {major}/{cfg.PlayerMajorWaypointLimit}, minor {minor}/{cfg.PlayerMinorWaypointLimit}. Major markers stay visible farther out.", true);
                return;
            }
            default:
                SendHelp(player, groupId);
                return;
        }
    }

    private void SendHelp(IServerPlayer player, int groupId)
    {
        int major = webMap.GetPlayerWaypointCount(player, "major");
        int minor = webMap.GetPlayerWaypointCount(player, "minor");
        var cfg = webMap.Config;
        Reply(player, groupId, "Web Map waypoint commands:", true);
        Reply(player, groupId, "/webmap waypoint add major [icon:<type>] <name> - place a major marker at your current position", true);
        Reply(player, groupId, "/webmap waypoint add minor [icon:<type>] <name> - place a minor marker at your current position", true);
        Reply(player, groupId, "Optional icons: icon:pin, icon:base, icon:settlement, icon:shop, icon:mine, icon:resource, icon:danger, icon:landmark, icon:road, icon:farm", true);
        Reply(player, groupId, "/webmap waypoint remove <name> - remove one of your own markers", true);
        Reply(player, groupId, "/webmap live off|on|status - hide/show yourself on the live player layer", true);
        Reply(player, groupId, $"Waypoint usage: major {major}/{cfg.PlayerMajorWaypointLimit}, minor {minor}/{cfg.PlayerMinorWaypointLimit}.", true);
    }

    private static void Reply(IServerPlayer player, int groupId, string text, bool success) =>
        player.SendMessage(groupId, text, success ? EnumChatType.CommandSuccess : EnumChatType.CommandError);
}
