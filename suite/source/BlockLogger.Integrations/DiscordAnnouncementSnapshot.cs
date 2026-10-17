namespace BlockLogger.Integrations;

public sealed class DiscordAnnouncementSnapshot
{
	public bool Enabled { get; set; }

	public string ChannelId { get; set; } = "";

	public string Format { get; set; } = "";
}
