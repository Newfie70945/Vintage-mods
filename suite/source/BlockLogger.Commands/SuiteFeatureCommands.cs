using System;
using System.Collections.Generic;
using System.Linq;
using BlockLogger.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Commands;

public sealed class SuiteFeatureCommands
{
	private readonly ICoreServerAPI api;

	private readonly SuiteFeatureConfig features;

	private readonly SuiteFeatureConfig activeFeatures;

	public SuiteFeatureCommands(ICoreServerAPI api, SuiteFeatureConfig features, SuiteFeatureConfig activeFeatures)
	{
		this.api = api;
		this.features = features;
		this.activeFeatures = activeFeatures;
	}

	public void Register()
	{
		api.RegisterCommand("nsefeatures", "Enable or disable Newfie's Server Essentials subsystems", "list | <feature> on|off | all on|off", OnCommand, "controlserver");
	}

	private void OnCommand(IServerPlayer player, int groupId, CmdArgs args)
	{
		string text = (args.PopWord() ?? "list").Trim().ToLowerInvariant();
		bool flag;
		switch (text)
		{
		case "list":
		case "status":
		case "help":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			SendList(player, groupId);
			return;
		}
		string text2 = (args.PopWord() ?? "").Trim().ToLowerInvariant();
		switch (text2)
		{
		case "on":
		case "off":
		case "true":
		case "false":
		case "1":
		case "0":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			Reply(player, groupId, "Usage: /nsefeatures <feature> on|off  or  /nsefeatures all on|off");
			return;
		}
		switch (text2)
		{
		case "on":
		case "true":
		case "1":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool flag2 = flag;
		if (text == "all")
		{
			features.SetAll(flag2);
			features.Save(api);
			Reply(player, groupId, "All optional suite features set to " + (flag2 ? "ON" : "OFF") + ". Restart the server to fully apply/unregister subsystems. /nsefeatures remains available either way.");
			return;
		}
		if (!features.TrySet(text, flag2, out string canonicalName))
		{
			Reply(player, groupId, "Unknown feature '" + text + "'. Use /nsefeatures list.");
			return;
		}
		features.Save(api);
		Reply(player, groupId, $"Feature '{canonicalName}' set to {(flag2 ? "ON" : "OFF")}. Restart the server to fully apply the change and remove/add its listeners, workers, timers, and network hooks.");
	}

	private void SendList(IServerPlayer player, int groupId)
	{
		IReadOnlyList<(string, bool)> entries = features.GetEntries();
		Dictionary<string, bool> active = activeFeatures.GetEntries().ToDictionary(((string Name, bool Enabled) x) => x.Name, ((string Name, bool Enabled) x) => x.Enabled, StringComparer.OrdinalIgnoreCase);
		string text = string.Join(", ", entries.Where(((string Name, bool Enabled) x) => x.Enabled).Select(((string Name, bool Enabled) x) => x.Name));
		string text2 = string.Join(", ", entries.Where(((string Name, bool Enabled) x) => !x.Enabled).Select(((string Name, bool Enabled) x) => x.Name));
		List<string> list = entries.Where(((string Name, bool Enabled) x) => active.TryGetValue(x.Name, out var value) && value != x.Enabled).Select(((string Name, bool Enabled) x) => $"{x.Name}:{(active[x.Name] ? "ON" : "OFF")}→{(x.Enabled ? "ON" : "OFF")}").ToList();
		Reply(player, groupId, "Suite feature gates (saved settings apply on next restart):");
		Reply(player, groupId, "NEXT BOOT ON: " + ((text.Length == 0) ? "none" : text));
		Reply(player, groupId, "NEXT BOOT OFF: " + ((text2.Length == 0) ? "none" : text2));
		Reply(player, groupId, "PENDING: " + ((list.Count == 0) ? "none" : string.Join(", ", list)));
		Reply(player, groupId, "Features: " + string.Join(", ", entries.Select(((string Name, bool Enabled) x) => x.Name)));
	}

	private void Reply(IServerPlayer player, int groupId, string message)
	{
		if (player == null)
		{
			api.Logger.Notification("[ServerEssentials] {0}", message);
			return;
		}
		player.SendMessage(groupId, message, EnumChatType.CommandSuccess);
	}
}
