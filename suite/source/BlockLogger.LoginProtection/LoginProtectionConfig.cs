using System;

namespace BlockLogger.LoginProtection;

public sealed class LoginProtectionConfig
{
	public const int CurrentSchemaVersion = 2;

	public int SchemaVersion { get; set; } = 2;

	public bool Enabled { get; set; } = true;

	public int ProtectionSeconds { get; set; } = 60;

	public bool ProtectOnJoin { get; set; } = true;

	public bool ProtectOnRespawn { get; set; } = true;

	public bool AnnounceOnJoin { get; set; } = true;

	public bool AnnounceOnEnd { get; set; } = true;

	public bool BlockPvP { get; set; } = true;

	public bool CancelOnAttack { get; set; } = true;

	public bool ProtectFallDamage { get; set; } = true;

	public bool ExtinguishFire { get; set; } = true;

	public bool RecoverSwimmingOnLogin { get; set; } = true;

	public void Clamp()
	{
		SchemaVersion = Math.Max(2, SchemaVersion);
		ProtectionSeconds = Math.Clamp(ProtectionSeconds, 1, 3600);
	}
}
