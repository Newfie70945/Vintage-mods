using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public sealed class CommunityMotdControlSnapshot
{
    public bool Available { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Version { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
}

public static class CommunityMotdControlBridge
{
    public const string EventName = "newfiescommunitymotd:control";

    public static CommunityMotdControlSnapshot Request(ICoreServerAPI api, string operation = "get", string configJson = "")
    {
        var tree = new TreeAttribute();
        tree.SetString("operation", operation ?? "get");
        tree.SetString("configJson", configJson ?? "");
        tree.SetBool("available", false);
        tree.SetBool("success", false);
        tree.SetString("message", "Newfie's Community MOTD is not installed or does not expose the Suite companion control bridge.");
        tree.SetString("version", "");
        tree.SetString("configJson", configJson ?? "");
        api.Event.PushEvent(EventName, tree);

        return new CommunityMotdControlSnapshot
        {
            Available = tree.GetBool("available", false),
            Success = tree.GetBool("success", false),
            Message = tree.GetString("message", ""),
            Version = tree.GetString("version", ""),
            ConfigJson = tree.GetString("configJson", "{}")
        };
    }
}
