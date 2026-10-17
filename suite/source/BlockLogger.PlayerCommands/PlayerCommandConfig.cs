using System;
using System.Collections.Generic;
using BlockLogger.Services;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class PlayerCommandConfig
{
	public const string ConfigFile = "NewfiesPlayerCommands.json";

	public const int CurrentSchemaVersion = 4;

	private static readonly HashSet<string> KnownCommandKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"spawn", "msg", "reply", "starterkit", "warp", "back", "home", "sethome", "delhome", "homes",
		"t2p", "t2m", "rtp"
	};

	private static readonly HashSet<string> TeleportCommandKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spawn", "back", "home", "warp", "t2p", "t2m", "rtp" };

	public int SchemaVersion { get; set; } = 4;

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

	public int WarpDefaultCooldownSeconds { get; set; } = 60;

	public int WarpDefaultWarmupSeconds { get; set; } = 5;

	public bool WarpDefaultCancelOnMove { get; set; } = true;

	public bool BackEnabled { get; set; } = true;

	public int BackCooldownSeconds { get; set; } = 30;

	public int BackWarmupSeconds { get; set; } = 5;

	public bool BackCancelOnMove { get; set; } = true;

	public bool BackTrackDeaths { get; set; } = true;

	public bool BackTrackTeleports { get; set; } = true;

	public bool HomesEnabled { get; set; } = true;

	public int HomeGlobalLimit { get; set; } = 2;

	public Dictionary<string, int> HomeRoleLimits { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

	public Dictionary<string, bool> CommandEnabled { get; set; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, Dictionary<string, TeleportRolePolicy>> TeleportRolePolicies { get; set; } = new Dictionary<string, Dictionary<string, TeleportRolePolicy>>(StringComparer.OrdinalIgnoreCase);

	public void Clamp()
	{
		SchemaVersion = Math.Max(4, SchemaVersion);
		SpawnCooldownSeconds = Math.Clamp(SpawnCooldownSeconds, 0, 86400);
		SpawnWarmupSeconds = Math.Clamp(SpawnWarmupSeconds, 0, 300);
		MaxPrivateMessageLength = Math.Clamp(MaxPrivateMessageLength, 32, 2000);
		StarterKitProvider = NormalizeStarterKitProvider(StarterKitProvider);
		WarpDefaultCooldownSeconds = Math.Clamp(WarpDefaultCooldownSeconds, 0, 86400);
		WarpDefaultWarmupSeconds = Math.Clamp(WarpDefaultWarmupSeconds, 0, 300);
		BackCooldownSeconds = Math.Clamp(BackCooldownSeconds, 0, 86400);
		BackWarmupSeconds = Math.Clamp(BackWarmupSeconds, 0, 300);
		HomeGlobalLimit = Math.Clamp(HomeGlobalLimit, 0, 100);
		HomeRoleLimits = NormalizeLimitMap(HomeRoleLimits);
		HomePlayerLimits = NormalizeLimitMap(HomePlayerLimits);
		HomeCooldownSeconds = Math.Clamp(HomeCooldownSeconds, 0, 86400);
		HomeWarmupSeconds = Math.Clamp(HomeWarmupSeconds, 0, 300);
		T2pCooldownSeconds = Math.Clamp(T2pCooldownSeconds, 0, 604800);
		T2mCooldownSeconds = Math.Clamp(T2mCooldownSeconds, 0, 604800);
		RtpMinRadius = Math.Clamp(RtpMinRadius, 0, 5000000);
		RtpMaxRadius = Math.Clamp(RtpMaxRadius, RtpMinRadius, 5000000);
		RtpCooldownSeconds = Math.Clamp(RtpCooldownSeconds, 0, 604800);
		RtpWarmupSeconds = Math.Clamp(RtpWarmupSeconds, 0, 300);
		RtpCostItemCode = (RtpCostItemCode ?? "").Trim();
		RtpCostQuantity = Math.Clamp(RtpCostQuantity, 1, 65535);
		RtpMaxSearchAttempts = Math.Clamp(RtpMaxSearchAttempts, 1, 100);
		if (string.IsNullOrWhiteSpace(RtpCostItemCode))
		{
			RtpCostEnabled = false;
		}
		if (CommandEnabled == null)
		{
			Dictionary<string, bool> dictionary = (CommandEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
		}
		Dictionary<string, bool> dictionary3 = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, bool> item in CommandEnabled)
		{
			string text = item.Key.Trim().ToLowerInvariant();
			if (KnownCommandKeys.Contains(text))
			{
				dictionary3[text] = item.Value;
			}
		}
		CommandEnabled = dictionary3;
		TeleportRolePolicies = NormalizeTeleportRolePolicies(TeleportRolePolicies);
	}

	public static bool IsKnownCommandKey(string? key)
	{
		return !string.IsNullOrWhiteSpace(key) && KnownCommandKeys.Contains(key.Trim());
	}

	private static Dictionary<string, Dictionary<string, TeleportRolePolicy>> NormalizeTeleportRolePolicies(Dictionary<string, Dictionary<string, TeleportRolePolicy>>? source)
	{
		Dictionary<string, Dictionary<string, TeleportRolePolicy>> dictionary = new Dictionary<string, Dictionary<string, TeleportRolePolicy>>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, Dictionary<string, TeleportRolePolicy>> item in source ?? new Dictionary<string, Dictionary<string, TeleportRolePolicy>>())
		{
			string text = (item.Key ?? "").Trim().ToLowerInvariant();
			if (!TeleportCommandKeys.Contains(text))
			{
				continue;
			}
			Dictionary<string, TeleportRolePolicy> dictionary2 = new Dictionary<string, TeleportRolePolicy>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<string, TeleportRolePolicy> item2 in item.Value ?? new Dictionary<string, TeleportRolePolicy>())
			{
				string text2 = (item2.Key ?? "").Trim().ToLowerInvariant();
				if (text2.Length != 0)
				{
					TeleportRolePolicy teleportRolePolicy = item2.Value ?? new TeleportRolePolicy();
					teleportRolePolicy.CooldownSeconds = Math.Clamp(teleportRolePolicy.CooldownSeconds, -1, 604800);
					dictionary2[text2] = teleportRolePolicy;
				}
			}
			if (dictionary2.Count > 0)
			{
				dictionary[text] = dictionary2;
			}
		}
		return dictionary;
	}

	public TeleportRolePolicyResolution ResolveTeleportRolePolicy(string command, string? roleCode, int defaultCooldownSeconds)
	{
		string key = (command ?? "").Trim().ToLowerInvariant();
		string text = (roleCode ?? "").Trim().ToLowerInvariant();
		int num = Math.Max(0, defaultCooldownSeconds);
		if (!TeleportRolePolicies.TryGetValue(key, out Dictionary<string, TeleportRolePolicy> value) || value == null)
		{
			return new TeleportRolePolicyResolution
			{
				Allowed = true,
				CooldownSeconds = num,
				RoleCode = text,
				Overridden = false
			};
		}
		TeleportRolePolicy value2 = null;
		if (text.Length > 0)
		{
			value.TryGetValue(text, out value2);
		}
		if (value2 == null)
		{
			value.TryGetValue("*", out value2);
		}
		if (value2 == null)
		{
			return new TeleportRolePolicyResolution
			{
				Allowed = true,
				CooldownSeconds = num,
				RoleCode = text,
				Overridden = false
			};
		}
		return new TeleportRolePolicyResolution
		{
			Allowed = value2.Allowed,
			CooldownSeconds = ((value2.CooldownSeconds >= 0) ? value2.CooldownSeconds : num),
			RoleCode = text,
			Overridden = true
		};
	}

	private static Dictionary<string, int> NormalizeLimitMap(Dictionary<string, int>? source)
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, int> item in source ?? new Dictionary<string, int>())
		{
			string text = (item.Key ?? "").Trim();
			if (text.Length != 0)
			{
				dictionary[text] = Math.Clamp(item.Value, 0, 100);
			}
		}
		return dictionary;
	}

	public static string NormalizeStarterKitProvider(string? value)
	{
		string text = (value ?? "Auto").Trim();
		if (text.Equals("nse", StringComparison.OrdinalIgnoreCase) || text.Equals("newfies", StringComparison.OrdinalIgnoreCase))
		{
			return "NSE";
		}
		if (text.Equals("th3", StringComparison.OrdinalIgnoreCase) || text.Equals("th3essentials", StringComparison.OrdinalIgnoreCase))
		{
			return "Th3Essentials";
		}
		return "Auto";
	}

	public static PlayerCommandConfig Load(ICoreServerAPI api)
	{
		PlayerCommandConfig playerCommandConfig = ModConfigRecovery.LoadOrNull<PlayerCommandConfig>(api, "NewfiesPlayerCommands.json", "PlayerCommands") ?? new PlayerCommandConfig();
		playerCommandConfig.Clamp();
		SuiteConfigStorage.Store(api, playerCommandConfig, "NewfiesPlayerCommands.json");
		return playerCommandConfig;
	}
}
