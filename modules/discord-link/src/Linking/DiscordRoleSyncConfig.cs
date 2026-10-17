namespace NewfiesServerEssentials.Discord.Linking;

public sealed class DiscordRoleSyncConfig
{
    /// <summary>Master switch for Discord role -> Vintage Story role synchronization.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How often linked online players are rechecked against Discord.</summary>
    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Roles that automatic synchronization is allowed to take over from when a player has
    /// never been managed before. This is deliberately conservative so manually assigned
    /// moderator/admin/custom roles are never replaced by Discord.
    /// </summary>
    public string[] EligibleBaseRoleCodes { get; set; } = ["suplayer"];

    /// <summary>Notify a player when Discord synchronization changes their in-game role.</summary>
    public bool NotifyPlayers { get; set; } = true;

    /// <summary>Write successful automatic role changes and manual-override detection to the server log.</summary>
    public bool LogChanges { get; set; } = true;

    /// <summary>
    /// Only mappings listed here participate. If a Discord member has more than one mapped role,
    /// the mapping with the highest Priority wins; ties keep config-file order.
    /// </summary>
    public List<DiscordRoleMapping> Mappings { get; set; } = new();
}

public sealed class DiscordRoleMapping
{
    public bool Enabled { get; set; } = true;
    public ulong DiscordRoleId { get; set; }
    public string GameRoleCode { get; set; } = "";
    public int Priority { get; set; }
}
