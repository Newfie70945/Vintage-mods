using System;
using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerCommandSettingsRequest
{
	public bool SpawnEnabled { get; set; } = true;

	public int SpawnCooldownSeconds { get; set; } = 60;

	public int SpawnWarmupSeconds { get; set; } = 5;

	public bool SpawnCancelOnMove { get; set; } = true;

	public bool PrivateMessagesEnabled { get; set; } = true;

	public bool ReplyEnabled { get; set; } = true;

	public bool RespectSuiteMutes { get; set; } = true;

	public int MaxPrivateMessageLength { get; set; } = 500;

	public bool StarterKitEnabled { get; set; } = true;

	public string StarterKitProvider { get; set; } = "Auto";

	public bool WarpsEnabled { get; set; } = true;

	public bool WarpCommandEnabled { get; set; } = true;

	public int? WarpDefaultCooldownSeconds { get; set; }

	public int? WarpDefaultWarmupSeconds { get; set; }

	public bool? WarpDefaultCancelOnMove { get; set; }

	public bool BackEnabled { get; set; } = true;

	public int BackCooldownSeconds { get; set; } = 30;

	public int BackWarmupSeconds { get; set; } = 5;

	public bool BackCancelOnMove { get; set; } = true;

	public bool BackTrackDeaths { get; set; } = true;

	public bool BackTrackTeleports { get; set; } = true;

	public bool HomesEnabled { get; set; } = true;

	public int HomeGlobalLimit { get; set; } = 2;

	public Dictionary<string, int>? HomeRoleLimits { get; set; }

	public Dictionary<string, int> HomePlayerLimits { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public int HomeCooldownSeconds { get; set; } = 60;

	public int HomeWarmupSeconds { get; set; } = 5;

	public bool HomeCancelOnMove { get; set; } = true;

	public bool T2pEnabled { get; set; } = true;

	public int T2pCooldownSeconds { get; set; } = 30;

	public bool T2mEnabled { get; set; } = true;

	public int T2mCooldownSeconds { get; set; } = 30;

	public bool RtpEnabled { get; set; } = true;

	public int RtpMinRadius { get; set; } = 1000;

	public int RtpMaxRadius { get; set; } = 10000;

	public int RtpCooldownSeconds { get; set; } = 1800;

	public int RtpWarmupSeconds { get; set; } = 10;

	public bool RtpCancelOnMove { get; set; } = true;

	public bool RtpCostEnabled { get; set; } = false;

	public string RtpCostItemCode { get; set; } = "game:gear-rusty";

	public int RtpCostQuantity { get; set; } = 1;

	public int RtpMaxSearchAttempts { get; set; } = 20;

	public bool RtpAvoidWaterAndLava { get; set; } = true;

	public bool RtpCenterOnPlayerPosition { get; set; } = false;

	public Dictionary<string, Dictionary<string, TeleportRolePolicy>>? TeleportRolePolicies { get; set; }

	public string? RolePolicyRevision { get; set; }
}
