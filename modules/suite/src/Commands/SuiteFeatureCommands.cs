using System;
using System.Linq;
using BlockLogger.Config;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace BlockLogger.Commands;

/// <summary>
/// Minimal always-available feature-gate command. This command remains registered
/// even when the Administration module is disabled so a server owner cannot lock
/// themselves out of re-enabling suite subsystems.
/// </summary>
public sealed class SuiteFeatureCommands
{
    private readonly ICoreServerAPI api;
    private readonly SuiteFeatureConfig features;
    private readonly SuiteFeatureConfig activeFeatures;

    public SuiteFeatureCommands(
        ICoreServerAPI api,
        SuiteFeatureConfig features,
        SuiteFeatureConfig activeFeatures)
    {
        this.api = api;
        this.features = features;
        this.activeFeatures = activeFeatures;
    }

    public void Register()
    {
#pragma warning disable CS0618
        api.RegisterCommand(
            "nsefeatures",
            "Enable or disable Newfie's Server Essentials subsystems",
            "list | <feature> on|off | all on|off",
            OnCommand,
            "controlserver");
#pragma warning restore CS0618
    }

    private void OnCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        var op = (args.PopWord() ?? "list").Trim().ToLowerInvariant();
        if (op is "list" or "status" or "help")
        {
            SendList(player, groupId);
            return;
        }

        var stateWord = (args.PopWord() ?? "").Trim().ToLowerInvariant();
        if (stateWord is not ("on" or "off" or "true" or "false" or "1" or "0"))
        {
            Reply(player, groupId, "Usage: /nsefeatures <feature> on|off  or  /nsefeatures all on|off");
            return;
        }

        bool enabled = stateWord is "on" or "true" or "1";
        if (op == "all")
        {
            features.SetAll(enabled);
            features.Save(api);
            Reply(player, groupId, $"All optional suite features set to {(enabled ? "ON" : "OFF")}. Restart the server to fully apply/unregister subsystems. /nsefeatures remains available either way.");
            return;
        }

        if (!features.TrySet(op, enabled, out string canonical))
        {
            Reply(player, groupId, $"Unknown feature '{op}'. Use /nsefeatures list.");
            return;
        }

        features.Save(api);
        Reply(player, groupId, $"Feature '{canonical}' set to {(enabled ? "ON" : "OFF")}. Restart the server to fully apply the change and remove/add its listeners, workers, timers, and network hooks.");
    }

    private void SendList(IServerPlayer player, int groupId)
    {
        var entries = features.GetEntries();
        var active = activeFeatures.GetEntries().ToDictionary(x => x.Name, x => x.Enabled, StringComparer.OrdinalIgnoreCase);
        var enabled = string.Join(", ", entries.Where(x => x.Enabled).Select(x => x.Name));
        var disabled = string.Join(", ", entries.Where(x => !x.Enabled).Select(x => x.Name));
        var pending = entries
            .Where(x => active.TryGetValue(x.Name, out bool current) && current != x.Enabled)
            .Select(x => $"{x.Name}:{(active[x.Name] ? "ON" : "OFF")}→{(x.Enabled ? "ON" : "OFF")}")
            .ToList();
        Reply(player, groupId, "Suite feature gates (saved settings apply on next restart):");
        Reply(player, groupId, "NEXT BOOT ON: " + (enabled.Length == 0 ? "none" : enabled));
        Reply(player, groupId, "NEXT BOOT OFF: " + (disabled.Length == 0 ? "none" : disabled));
        Reply(player, groupId, "PENDING: " + (pending.Count == 0 ? "none" : string.Join(", ", pending)));
        Reply(player, groupId, "Features: " + string.Join(", ", entries.Select(x => x.Name)));
    }

    private void Reply(IServerPlayer player, int groupId, string message)
    {
        if (player == null) { api.Logger.Notification("[ServerEssentials] {0}", message); return; }
        player.SendMessage(groupId, message, EnumChatType.CommandSuccess);
    }
}
