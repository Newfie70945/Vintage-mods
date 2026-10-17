using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace BlockLogger.Integrations;

public static class CommunityMotdControlBridge
{
	public const string EventName = "newfiescommunitymotd:control";

	public static CommunityMotdControlSnapshot Request(ICoreServerAPI api, string operation = "get", string configJson = "")
	{
		TreeAttribute treeAttribute = new TreeAttribute();
		treeAttribute.SetString("operation", operation ?? "get");
		treeAttribute.SetString("configJson", configJson ?? "");
		treeAttribute.SetBool("available", value: false);
		treeAttribute.SetBool("success", value: false);
		treeAttribute.SetString("message", "Newfie's Community MOTD is not installed or does not expose the Suite companion control bridge.");
		treeAttribute.SetString("version", "");
		treeAttribute.SetString("configJson", configJson ?? "");
		api.Event.PushEvent("newfiescommunitymotd:control", treeAttribute);
		return new CommunityMotdControlSnapshot
		{
			Available = treeAttribute.GetBool("available"),
			Success = treeAttribute.GetBool("success"),
			Message = treeAttribute.GetString("message", ""),
			Version = treeAttribute.GetString("version", ""),
			ConfigJson = treeAttribute.GetString("configJson", "{}")
		};
	}
}
