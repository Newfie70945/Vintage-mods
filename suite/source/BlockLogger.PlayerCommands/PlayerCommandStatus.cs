namespace BlockLogger.PlayerCommands;

public sealed class PlayerCommandStatus
{
	public string Key { get; set; } = "";

	public string Command { get; set; } = "";

	public bool Enabled { get; set; }

	public bool IndividuallyEnabled { get; set; } = true;

	public string RootProvider { get; set; } = "";

	public bool RootOwnedBySuite { get; set; }

	public string FallbackCommand { get; set; } = "";

	public string Description { get; set; } = "";

	public string RequiredPrivilege { get; set; } = "";
}
