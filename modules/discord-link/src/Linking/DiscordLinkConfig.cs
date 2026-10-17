namespace NewfiesServerEssentials.Discord.Linking;

public sealed class DiscordLinkConfig
{
    public bool Enabled { get; set; } = false;

    // Human-entered one-time codes. Ambiguous characters are excluded by the generator.
    public int CodeLength { get; set; } = 8;
    public int CodeExpirySeconds { get; set; } = 300;
    public int CodeCooldownSeconds { get; set; } = 30;

    // Brute-force protection applied to Discord users submitting invalid codes.
    public int MaxFailedAttemptsPerDiscordUser { get; set; } = 8;
    public int FailedAttemptWindowSeconds { get; set; } = 300;

    // Default is a strict one Discord account <-> one Vintage Story PlayerUID mapping.
    public bool AllowOneDiscordAccountToLinkMultiplePlayers { get; set; } = false;

    // Keep successful link/unlink operations visible in the Vintage Story server log.
    public bool LogLinkChanges { get; set; } = false;

    // Optional linked-account role synchronization. Disabled by default and configured only
    // for explicitly mapped Discord roles.
    public DiscordRoleSyncConfig RoleSync { get; set; } = new();
}
