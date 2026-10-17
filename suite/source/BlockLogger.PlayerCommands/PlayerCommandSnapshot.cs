using System;
using System.Collections.Generic;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerCommandSnapshot
{
	public bool Available { get; set; } = true;

	public bool Th3EssentialsDetected { get; set; }

	public bool SpawnEnabled { get; set; }

	public int SpawnCooldownSeconds { get; set; }

	public int SpawnWarmupSeconds { get; set; }

	public bool SpawnCancelOnMove { get; set; }

	public bool PrivateMessagesEnabled { get; set; }

	public bool ReplyEnabled { get; set; }

	public bool RespectSuiteMutes { get; set; }

	public int MaxPrivateMessageLength { get; set; }

	public bool StarterKitEnabled { get; set; }

	public string StarterKitProvider { get; set; } = "Auto";

	public string StarterKitEffectiveProvider { get; set; } = "NSE";

	public bool StarterKitConfigured { get; set; }

	public int StarterKitItemCount { get; set; }

	public int StarterKitClaimedCount { get; set; }

	public bool WarpsEnabled { get; set; }

	public bool WarpCommandEnabled { get; set; }

	public int WarpDefaultCooldownSeconds { get; set; }

	public int WarpDefaultWarmupSeconds { get; set; }

	public bool WarpDefaultCancelOnMove { get; set; }

	public int WarpCount { get; set; }

	public bool BackEnabled { get; set; }

	public int BackCooldownSeconds { get; set; }

	public int BackWarmupSeconds { get; set; }

	public bool BackCancelOnMove { get; set; }

	public bool BackTrackDeaths { get; set; }

	public bool BackTrackTeleports { get; set; }

	public bool HomesEnabled { get; set; }

	public int HomeGlobalLimit { get; set; }

	public Dictionary<string, int> HomeRoleLimits { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, int> HomePlayerLimits { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	public int HomeCooldownSeconds { get; set; }

	public int HomeWarmupSeconds { get; set; }

	public bool HomeCancelOnMove { get; set; }

	public bool T2pEnabled { get; set; }

	public int T2pCooldownSeconds { get; set; }

	public bool T2mEnabled { get; set; }

	public int T2mCooldownSeconds { get; set; }

	public bool RtpEnabled { get; set; }

	public int RtpMinRadius { get; set; }

	public int RtpMaxRadius { get; set; }

	public int RtpCooldownSeconds { get; set; }

	public int RtpWarmupSeconds { get; set; }

	public bool RtpCancelOnMove { get; set; }

	public bool RtpCostEnabled { get; set; }

	public string RtpCostItemCode { get; set; } = "";

	public int RtpCostQuantity { get; set; }

	public int RtpMaxSearchAttempts { get; set; }

	public bool RtpAvoidWaterAndLava { get; set; }

	public bool RtpCenterOnPlayerPosition { get; set; }

	public Dictionary<string, Dictionary<string, TeleportRolePolicy>> TeleportRolePolicies { get; set; } = new Dictionary<string, Dictionary<string, TeleportRolePolicy>>(StringComparer.OrdinalIgnoreCase);

	public string RolePolicyRevision { get; set; } = "";

	public List<PermissionRoleSummary> AvailableRoles { get; set; } = new List<PermissionRoleSummary>();

	public List<PlayerCommandStatus> Commands { get; set; } = new List<PlayerCommandStatus>();
}
