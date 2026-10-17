using System;

namespace BlockLogger.LoginProtection;

public sealed class LoginProtectionConfig
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool Enabled { get; set; } = true;
    public int ProtectionSeconds { get; set; } = 60;

    public bool ProtectOnJoin { get; set; } = true;
    public bool ProtectOnRespawn { get; set; } = true;

    // Keep the legacy property names so old loginprotection.json files migrate cleanly.
    public bool AnnounceOnJoin { get; set; } = true;
    public bool AnnounceOnEnd { get; set; } = true;

    // Prevent a protected player from damaging another player while shielded.
    public bool BlockPvP { get; set; } = true;

    // Attacking anything ends the attacker's protection.
    public bool CancelOnAttack { get; set; } = true;

    // Login protection normally blocks all damage. This switch lets admins opt
    // fall damage back in if they do not want the shield to cover falls.
    public bool ProtectFallDamage { get; set; } = true;

    // If fire damage arrives while protected, extinguish the player as well as
    // suppressing the damage so fire does not immediately resume after expiry.
    public bool ExtinguishFire { get; set; } = true;

    // If a player reconnects while their body is in liquid, force the server-side
    // swim/detached movement state long enough for normal liquid physics to take over.
    public bool RecoverSwimmingOnLogin { get; set; } = true;

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        ProtectionSeconds = Math.Clamp(ProtectionSeconds, 1, 3600);
    }
}
