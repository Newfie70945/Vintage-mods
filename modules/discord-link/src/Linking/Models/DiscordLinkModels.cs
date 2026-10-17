namespace NewfiesServerEssentials.Discord.Linking.Models;

public sealed class DiscordLinkDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, DiscordLinkRecord> LinksByPlayerUid { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DiscordLinkRecord
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string DiscordUserId { get; set; } = "";
    public string DiscordUserName { get; set; } = "";
    public long LinkedAtUnixMilliseconds { get; set; }
}

public sealed class PendingLinkCode
{
    public string PlayerUid { get; init; } = "";
    public string PlayerName { get; init; } = "";
    public string CodeHash { get; init; } = "";
    public long CreatedAtUnixMilliseconds { get; init; }
    public long ExpiresAtUnixMilliseconds { get; init; }
}
