namespace NewfiesServerEssentials.Discord.Linking.Models;

public sealed class DiscordRoleSyncDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, DiscordRoleSyncState> StatesByPlayerUid { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DiscordRoleSyncState
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string DiscordUserId { get; set; } = "";

    // The exact in-game role that existed before Discord role sync first took ownership.
    // It is restored only while the player is still on the role we assigned.
    public string PreviousRoleCode { get; set; } = "";
    public string ManagedRoleCode { get; set; } = "";
    public ulong DiscordRoleId { get; set; }

    // If an administrator manually moves the player away from ManagedRoleCode, automatic
    // synchronization suspends for that player and will never change the manual role back.
    public bool SuspendedByManualOverride { get; set; }
    public string ManualRoleCode { get; set; } = "";

    public long LastCheckedUnixMilliseconds { get; set; }
    public long LastAppliedUnixMilliseconds { get; set; }
}
