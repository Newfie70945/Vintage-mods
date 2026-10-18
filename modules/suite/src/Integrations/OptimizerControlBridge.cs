using System.Text.Json;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public static class OptimizerControlBridge
{
    public static object Request(ICoreServerAPI api, string operation = "get", string settings = "{}")
    {
        var tree = new TreeAttribute();
        tree.SetString("operation", operation); tree.SetString("settings", settings);
        tree.SetBool("success", false); tree.SetString("message", "Install Newfies Server Optimizer 0.3.0 or later alongside the Suite and restart the server.");
        api.Event.PushEvent("newfiesse:optimizer-control-v1", tree);
        string json = tree.GetString("snapshot", "");
        JsonElement? snapshot = null;
        if (!string.IsNullOrWhiteSpace(json)) { using var doc = JsonDocument.Parse(json); snapshot = doc.RootElement.Clone(); }
        return new { available = snapshot != null, success = tree.GetBool("success", false), message = tree.GetString("message", ""), optimizer = snapshot };
    }
}
