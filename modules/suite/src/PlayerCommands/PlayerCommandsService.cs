using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BlockLogger.Admin;
using BlockLogger.AntiCheat;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;


public sealed class TeleportRolePolicy
{
    public bool Allowed { get; set; } = true;
    // -1 inherits the command's normal cooldown. 0 disables cooldown for this role.
    public int CooldownSeconds { get; set; } = -1;
}

public sealed class TeleportRolePolicyResolution
{
    public bool Allowed { get; init; } = true;
    public int CooldownSeconds { get; init; }
    public string RoleCode { get; init; } = "";
    public bool Overridden { get; init; }
}

public sealed class PlayerCommandConfig
{
    public const string ConfigFile = "NewfiesPlayerCommands.json";
    public const int CurrentSchemaVersion = 4;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
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
    public Dictionary<string, int> HomeRoleLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> HomePlayerLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    // Individual command switches. Broad subsystem switches above remain master gates.
    public Dictionary<string, bool> CommandEnabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Optional role-specific access/cooldown policy for commands that actually teleport a player.
    // Missing role entries preserve the command's normal access and cooldown for backwards compatibility.
    // Supported keys: spawn, back, home, warp, t2p, t2m, rtp. "*" may be used as a fallback for all roles.
    public Dictionary<string, Dictionary<string, TeleportRolePolicy>> TeleportRolePolicies { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        SpawnCooldownSeconds = Math.Clamp(SpawnCooldownSeconds, 0, 86_400);
        SpawnWarmupSeconds = Math.Clamp(SpawnWarmupSeconds, 0, 300);
        MaxPrivateMessageLength = Math.Clamp(MaxPrivateMessageLength, 32, 2_000);
        StarterKitProvider = NormalizeStarterKitProvider(StarterKitProvider);
        WarpDefaultCooldownSeconds = Math.Clamp(WarpDefaultCooldownSeconds, 0, 86_400);
        WarpDefaultWarmupSeconds = Math.Clamp(WarpDefaultWarmupSeconds, 0, 300);
        BackCooldownSeconds = Math.Clamp(BackCooldownSeconds, 0, 86_400);
        BackWarmupSeconds = Math.Clamp(BackWarmupSeconds, 0, 300);
        HomeGlobalLimit = Math.Clamp(HomeGlobalLimit, 0, 100);
        HomeRoleLimits = NormalizeLimitMap(HomeRoleLimits);
        HomePlayerLimits = NormalizeLimitMap(HomePlayerLimits);
        HomeCooldownSeconds = Math.Clamp(HomeCooldownSeconds, 0, 86_400);
        HomeWarmupSeconds = Math.Clamp(HomeWarmupSeconds, 0, 300);
        T2pCooldownSeconds = Math.Clamp(T2pCooldownSeconds, 0, 604_800);
        T2mCooldownSeconds = Math.Clamp(T2mCooldownSeconds, 0, 604_800);
        RtpMinRadius = Math.Clamp(RtpMinRadius, 0, 5_000_000);
        RtpMaxRadius = Math.Clamp(RtpMaxRadius, RtpMinRadius, 5_000_000);
        RtpCooldownSeconds = Math.Clamp(RtpCooldownSeconds, 0, 604_800);
        RtpWarmupSeconds = Math.Clamp(RtpWarmupSeconds, 0, 300);
        RtpCostItemCode = (RtpCostItemCode ?? "").Trim();
        RtpCostQuantity = Math.Clamp(RtpCostQuantity, 1, 65_535);
        RtpMaxSearchAttempts = Math.Clamp(RtpMaxSearchAttempts, 1, 100);
        if (string.IsNullOrWhiteSpace(RtpCostItemCode)) RtpCostEnabled = false;
        CommandEnabled ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var normalizedCommandEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in CommandEnabled)
        {
            string normalized = entry.Key.Trim().ToLowerInvariant();
            if (KnownCommandKeys.Contains(normalized))
                normalizedCommandEnabled[normalized] = entry.Value;
        }
        CommandEnabled = normalizedCommandEnabled;
        TeleportRolePolicies = NormalizeTeleportRolePolicies(TeleportRolePolicies);
    }

    private static readonly HashSet<string> KnownCommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawn", "msg", "reply", "starterkit", "warp", "back", "home", "sethome", "delhome", "homes", "t2p", "t2m", "rtp"
    };

    public static bool IsKnownCommandKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && KnownCommandKeys.Contains(key.Trim());

    private static readonly HashSet<string> TeleportCommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawn", "back", "home", "warp", "t2p", "t2m", "rtp"
    };

    private static Dictionary<string, Dictionary<string, TeleportRolePolicy>> NormalizeTeleportRolePolicies(
        Dictionary<string, Dictionary<string, TeleportRolePolicy>>? source)
    {
        var result = new Dictionary<string, Dictionary<string, TeleportRolePolicy>>(StringComparer.OrdinalIgnoreCase);
        foreach (var commandEntry in source ?? new Dictionary<string, Dictionary<string, TeleportRolePolicy>>())
        {
            string command = (commandEntry.Key ?? "").Trim().ToLowerInvariant();
            if (!TeleportCommandKeys.Contains(command)) continue;
            var roles = new Dictionary<string, TeleportRolePolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var roleEntry in commandEntry.Value ?? new Dictionary<string, TeleportRolePolicy>())
            {
                string role = (roleEntry.Key ?? "").Trim().ToLowerInvariant();
                if (role.Length == 0) continue;
                var policy = roleEntry.Value ?? new TeleportRolePolicy();
                policy.CooldownSeconds = Math.Clamp(policy.CooldownSeconds, -1, 604_800);
                roles[role] = policy;
            }
            if (roles.Count > 0) result[command] = roles;
        }
        return result;
    }

    public TeleportRolePolicyResolution ResolveTeleportRolePolicy(string command, string? roleCode, int defaultCooldownSeconds)
    {
        string cmd = (command ?? "").Trim().ToLowerInvariant();
        string role = (roleCode ?? "").Trim().ToLowerInvariant();
        int fallbackCooldown = Math.Max(0, defaultCooldownSeconds);
        if (!TeleportRolePolicies.TryGetValue(cmd, out var roleMap) || roleMap == null)
            return new TeleportRolePolicyResolution { Allowed = true, CooldownSeconds = fallbackCooldown, RoleCode = role, Overridden = false };

        TeleportRolePolicy? policy = null;
        if (role.Length > 0) roleMap.TryGetValue(role, out policy);
        if (policy == null) roleMap.TryGetValue("*", out policy);
        if (policy == null)
            return new TeleportRolePolicyResolution { Allowed = true, CooldownSeconds = fallbackCooldown, RoleCode = role, Overridden = false };

        return new TeleportRolePolicyResolution
        {
            Allowed = policy.Allowed,
            CooldownSeconds = policy.CooldownSeconds >= 0 ? policy.CooldownSeconds : fallbackCooldown,
            RoleCode = role,
            Overridden = true
        };
    }

    private static Dictionary<string, int> NormalizeLimitMap(Dictionary<string, int>? source)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in source ?? new Dictionary<string, int>())
        {
            string key = (entry.Key ?? "").Trim();
            if (key.Length == 0) continue;
            result[key] = Math.Clamp(entry.Value, 0, 100);
        }
        return result;
    }

    public static string NormalizeStarterKitProvider(string? value)
    {
        string v = (value ?? "Auto").Trim();
        if (v.Equals("nse", StringComparison.OrdinalIgnoreCase) || v.Equals("newfies", StringComparison.OrdinalIgnoreCase)) return "NSE";
        if (v.Equals("th3", StringComparison.OrdinalIgnoreCase) || v.Equals("th3essentials", StringComparison.OrdinalIgnoreCase)) return "Th3Essentials";
        return "Auto";
    }

    public static PlayerCommandConfig Load(ICoreServerAPI api)
    {
        var cfg = ModConfigRecovery.LoadOrNull<PlayerCommandConfig>(api, ConfigFile, "PlayerCommands") ?? new PlayerCommandConfig();
        cfg.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, cfg, ConfigFile);
        return cfg;
    }
}

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
    public Dictionary<string, int> HomePlayerLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    public string? RolePolicyRevision {get;set;}
}

public sealed class StarterKitItemDefinition
{
    public int Slot { get; set; }
    public string StackBase64 { get; set; } = "";
    public string Code { get; set; } = "";
    public int StackSize { get; set; }
}

public sealed class StarterKitDefinition
{
    public const string ConfigFile = "NewfiesStarterKit.json";
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string UpdatedBy { get; set; } = "";
    public long UpdatedAtUnixMilliseconds { get; set; }
    public List<StarterKitItemDefinition> Items { get; set; } = new();

    public static StarterKitDefinition Load(ICoreServerAPI api)
    {
        var value = ModConfigRecovery.LoadOrNull<StarterKitDefinition>(api, ConfigFile, "StarterKit") ?? new StarterKitDefinition();
        value.Items ??= new List<StarterKitItemDefinition>();
        value.Items = value.Items.Where(x => x != null && x.Slot >= 0 && !string.IsNullOrWhiteSpace(x.StackBase64)).OrderBy(x => x.Slot).ToList();
        BlockLogger.Services.SuiteConfigStorage.Store(api, value, ConfigFile);
        return value;
    }
}

public sealed class StarterKitUsageDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> ClaimedPlayerUids { get; set; } = new();
}

public sealed class PlayerCommandStatus
{
    public string Key { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Enabled { get; set; }
    public bool IndividuallyEnabled { get; set; } = true;
    public string RootProvider { get; set; } = "";
    public bool RootOwnedBySuite { get; set; }
    public string FallbackCommand { get; set; } = "";
    public string Description { get; set; } = "";
    public string RequiredPrivilege { get; set; } = "";
}

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
    public Dictionary<string, int> HomeRoleLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> HomePlayerLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    public Dictionary<string, Dictionary<string, TeleportRolePolicy>> TeleportRolePolicies { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string RolePolicyRevision {get;set;}="";
    public List<PermissionRoleSummary> AvailableRoles { get; set; } = new();
    public List<PlayerCommandStatus> Commands { get; set; } = new();
}


public sealed class PermissionRoleSummary
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int PrivilegeLevel { get; set; }
    public List<string> Privileges { get; set; } = new();
}

public sealed class PermissionPlayerSummary
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string RoleCode { get; set; } = "";
    public List<string> EffectivePrivileges { get; set; } = new();
    public List<string> PermanentPrivileges { get; set; } = new();
    public List<string> DeniedPrivileges { get; set; } = new();
}

public sealed class PermissionsManagerSnapshot
{
    public List<PermissionRoleSummary> Roles { get; set; } = new();
    public List<PermissionPlayerSummary> OnlinePlayers { get; set; } = new();
    public List<string> KnownPrivileges { get; set; } = new();
}

public sealed class PermissionChangeRequest
{
    public string PlayerUid { get; set; } = "";
    public string Action { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class PermissionChangeResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public PermissionsManagerSnapshot Snapshot { get; set; } = new();
}

public sealed class PlayerCommandSaveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public PlayerCommandSnapshot Snapshot { get; set; } = new();
}

/// <summary>
/// Player-facing convenience commands owned by Server Essentials.
/// Root aliases are deliberately conditional so Th3Essentials and any other
/// already-registered provider can retain command ownership without startup collisions.
/// Available NSE aliases are registered once at startup and their enabled state is
/// evaluated at execution time, allowing WebPanel changes to take effect immediately.
/// </summary>
public sealed class PlayerCommandsService : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly Func<AdminService?> getAdmin;
    private readonly PlayerCommandConfig config;
    private readonly Dictionary<string, string> lastCorrespondentByUid = new(StringComparer.OrdinalIgnoreCase);
    private const string StarterKitUsageSaveKey = "newfiesserveressentials:starterkit-usage-v1";
    private readonly HashSet<string> suiteOwnedRootCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> starterKitClaims = new(StringComparer.OrdinalIgnoreCase);
    private StarterKitDefinition starterKit;
    private readonly PlayerTeleportService teleports;
    private readonly WarpService warps;
    private bool disposed;

    public PlayerCommandsService(ICoreServerAPI api, Func<AdminService?> getAdmin)
    {
        this.api = api;
        this.getAdmin = getAdmin;
        config = PlayerCommandConfig.Load(api);
        starterKit = StarterKitDefinition.Load(api);
        teleports = new PlayerTeleportService(api, () => config, uid => getAdmin()?.IsJailed(uid) == true);
        warps = new WarpService(api, () => config, (player, origin, reason) => teleports.RecordTeleportOrigin(player, origin, reason), uid => getAdmin()?.IsJailed(uid) == true);
        LoadStarterKitUsage();
    }

    public PlayerCommandConfig Config => config;
    public WarpService Warps => warps;
    public PlayerTeleportService Teleports => teleports;

    private bool HasTh3Essentials => api.ModLoader.Mods.Any(mod =>
        mod?.Info != null && string.Equals(mod.Info.ModID, "th3essentials", StringComparison.OrdinalIgnoreCase));

    private string StarterKitEffectiveProvider
    {
        get
        {
            string provider = PlayerCommandConfig.NormalizeStarterKitProvider(config.StarterKitProvider);
            if (provider == "NSE") return "NSE";
            if (provider == "Th3Essentials") return "Th3Essentials";
            return HasTh3Essentials ? "Th3Essentials" : "NSE";
        }
    }

    private bool UsesNseStarterKit => StarterKitEffectiveProvider == "NSE";

    public void Start()
    {
        bool th3 = HasTh3Essentials;
        if (th3)
        {
            api.Logger.Notification(
                "[ServerEssentials/PlayerCommands] Th3Essentials detected. NSE will not register root /spawn, /msg, /r, /starterkit, /setstarterkit or starter-kit reset aliases. Th3Essentials keeps those roots. Warp aliases are registered only when free. NSE fallbacks remain available under /nse.");
        }
        else
        {
            // Register free aliases regardless of their current enabled state. The handlers
            // enforce the live setting, which lets the WebPanel toggle commands without a restart.
            SafeRegisterRoot("spawn", "Teleport to the global world spawn", "", HandleSpawn, Privilege.chat);
            SafeRegisterRoot("msg", "Send a private message to an online player", "<player> <message>", HandleMsg, Privilege.chat);
            SafeRegisterRoot("r", "Reply to your last private-message correspondent", "<message>", HandleReply, Privilege.chat);
            SafeRegisterRoot("starterkit", "Claim the configured starter kit once", "", HandleStarterKit, Privilege.chat);
            SafeRegisterRoot("setstarterkit", "Set the NSE starter kit from your hotbar", "", HandleSetStarterKit, Privilege.controlserver);
            SafeRegisterRoot("resetstarterkitusage", "Reset starter-kit usage for one online player", "<player>", HandleResetStarterKitUsage, Privilege.controlserver);
            SafeRegisterRoot("resetstarterkitusageall", "Reset starter-kit usage for all players", "", HandleResetStarterKitUsageAll, Privilege.controlserver);
        }

        // Player teleport roots are compatibility-safe. Any existing provider keeps the root
        // command and NSE remains reachable through the /nse fallback.
        SafeRegisterRoot("back", "Return to your last death or Suite teleport position", "", HandleBack, Privilege.chat);
        SafeRegisterRoot("home", "Teleport to one of your homes", "", HandleHome, Privilege.chat);
        SafeRegisterRoot("sethome", "Set or move one of your homes", "", HandleSetHome, Privilege.chat);
        SafeRegisterRoot("delhome", "Delete one of your homes", "", HandleDeleteHome, Privilege.chat);
        SafeRegisterRoot("homes", "List your homes and current home limit", "", HandleHomes, Privilege.chat);
        SafeRegisterRoot("t2p", "Teleport yourself to an online player", "<player>", HandleT2p, Privilege.chat);
        SafeRegisterRoot("t2m", "Request an online player teleport to you", "<player>", HandleT2m, Privilege.chat);
        SafeRegisterRoot("tpaccept", "Accept a pending /t2m teleport request", "", HandleTpAccept, Privilege.chat);
        SafeRegisterRoot("rtp", "Teleport to a safe random location", "", HandleRtp, Privilege.chat);

        // Warp roots are compatibility-safe regardless of Th3Essentials. If another mod owns
        // any alias, NSE leaves it untouched and the /nse fallback remains available.
        SafeRegisterRoot("warp", "Teleport to a configured server warp", "<name>", HandleWarp, Privilege.chat);
        SafeRegisterRoot("warps", "List configured server warps", "", HandleWarps, Privilege.chat);
        SafeRegisterRoot("warpinfo", "Show details for a configured server warp", "<name>", HandleWarpInfo, Privilege.chat);
        SafeRegisterRoot("setwarp", "Create or move a server warp", "<name> [itemcode] [quantity]", HandleSetWarp, Privilege.controlserver);
        SafeRegisterRoot("delwarp", "Delete a server warp", "<name>", HandleDeleteWarp, Privilege.controlserver);
        SafeRegisterRoot("setwarpcost", "Set or clear a warp item cost", "<warp> <itemcode|none> [quantity]", HandleSetWarpCost, Privilege.controlserver);
        SafeRegisterRoot("setwarpcooldown", "Set a per-warp cooldown override", "<warp> <seconds|default>", HandleSetWarpCooldown, Privilege.controlserver);
        SafeRegisterRoot("setwarpwarmup", "Set a per-warp warm-up override", "<warp> <seconds|default>", HandleSetWarpWarmup, Privilege.controlserver);
        SafeRegisterRoot("setwarpmove", "Set movement cancellation for a warp", "<warp> <on|off|default>", HandleSetWarpMovement, Privilege.controlserver);
        SafeRegisterRoot("warpreload", "Reload warp definitions from disk", "", HandleReloadWarps, Privilege.controlserver);

        teleports.Start();
        warps.Start();
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Logger.Notification(
            "[ServerEssentials/PlayerCommands] Started. spawn={0}, msg={1}, reply={2}, starterkit={3}, starterKitProvider={4}, spawnCooldown={5}s.",
            config.SpawnEnabled, config.PrivateMessagesEnabled, config.ReplyEnabled, config.StarterKitEnabled, StarterKitEffectiveProvider, config.SpawnCooldownSeconds);
    }

    private bool IsIndividualCommandEnabled(string key) =>
        !config.CommandEnabled.TryGetValue(key, out bool enabled) || enabled;

    private bool IsCommandEnabled(string key, bool masterEnabled) =>
        masterEnabled && IsIndividualCommandEnabled(key);

    public PlayerCommandSaveResult SetCommandEnabled(string key, bool enabled)
    {
        key = (key ?? "").Trim().ToLowerInvariant();
        if (!PlayerCommandConfig.IsKnownCommandKey(key))
            return new PlayerCommandSaveResult { Success = false, Message = "Unknown player command.", Snapshot = GetSnapshot() };
        config.CommandEnabled[key] = enabled;
        config.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, PlayerCommandConfig.ConfigFile);
        return new PlayerCommandSaveResult
        {
            Success = true,
            Message = $"/{(key == "reply" ? "r" : key)} is now {(enabled ? "enabled" : "disabled")} within its subsystem.",
            Snapshot = GetSnapshot()
        };
    }

    public PlayerCommandSnapshot GetSnapshot()
    {
        bool th3 = HasTh3Essentials;
        return new PlayerCommandSnapshot
        {
            Available = true,
            Th3EssentialsDetected = th3,
            SpawnEnabled = config.SpawnEnabled,
            SpawnCooldownSeconds = config.SpawnCooldownSeconds,
            SpawnWarmupSeconds = config.SpawnWarmupSeconds,
            SpawnCancelOnMove = config.SpawnCancelOnMove,
            PrivateMessagesEnabled = config.PrivateMessagesEnabled,
            ReplyEnabled = config.ReplyEnabled,
            RespectSuiteMutes = config.RespectSuiteMutes,
            MaxPrivateMessageLength = config.MaxPrivateMessageLength,
            StarterKitEnabled = config.StarterKitEnabled,
            StarterKitProvider = PlayerCommandConfig.NormalizeStarterKitProvider(config.StarterKitProvider),
            StarterKitEffectiveProvider = StarterKitEffectiveProvider,
            StarterKitConfigured = starterKit.Items.Count > 0,
            StarterKitItemCount = starterKit.Items.Count,
            StarterKitClaimedCount = starterKitClaims.Count,
            WarpsEnabled = config.WarpsEnabled,
            WarpCommandEnabled = config.WarpCommandEnabled,
            WarpDefaultCooldownSeconds = config.WarpDefaultCooldownSeconds,
            WarpDefaultWarmupSeconds = config.WarpDefaultWarmupSeconds,
            WarpDefaultCancelOnMove = config.WarpDefaultCancelOnMove,
            WarpCount = warps.GetSnapshot().Warps.Count,
            BackEnabled = config.BackEnabled,
            BackCooldownSeconds = config.BackCooldownSeconds,
            BackWarmupSeconds = config.BackWarmupSeconds,
            BackCancelOnMove = config.BackCancelOnMove,
            BackTrackDeaths = config.BackTrackDeaths,
            BackTrackTeleports = config.BackTrackTeleports,
            HomesEnabled = config.HomesEnabled,
            HomeGlobalLimit = config.HomeGlobalLimit,
            RolePolicyRevision=BlockLogger.Roles.RoleManagementService.PolicyRevision(config),
            HomeRoleLimits = new Dictionary<string, int>(config.HomeRoleLimits, StringComparer.OrdinalIgnoreCase),
            HomePlayerLimits = new Dictionary<string, int>(config.HomePlayerLimits, StringComparer.OrdinalIgnoreCase),
            HomeCooldownSeconds = config.HomeCooldownSeconds,
            HomeWarmupSeconds = config.HomeWarmupSeconds,
            HomeCancelOnMove = config.HomeCancelOnMove,
            T2pEnabled = config.T2pEnabled,
            T2pCooldownSeconds = config.T2pCooldownSeconds,
            T2mEnabled = config.T2mEnabled,
            T2mCooldownSeconds = config.T2mCooldownSeconds,
            RtpEnabled = config.RtpEnabled,
            RtpMinRadius = config.RtpMinRadius,
            RtpMaxRadius = config.RtpMaxRadius,
            RtpCooldownSeconds = config.RtpCooldownSeconds,
            RtpWarmupSeconds = config.RtpWarmupSeconds,
            RtpCancelOnMove = config.RtpCancelOnMove,
            RtpCostEnabled = config.RtpCostEnabled,
            RtpCostItemCode = config.RtpCostItemCode,
            RtpCostQuantity = config.RtpCostQuantity,
            RtpMaxSearchAttempts = config.RtpMaxSearchAttempts,
            RtpAvoidWaterAndLava = config.RtpAvoidWaterAndLava,
            RtpCenterOnPlayerPosition = config.RtpCenterOnPlayerPosition,
            TeleportRolePolicies = config.TeleportRolePolicies.ToDictionary(
                x => x.Key,
                x => new Dictionary<string, TeleportRolePolicy>(x.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            AvailableRoles = (api.Server.Config.Roles ?? new List<IPlayerRole>())
                .Where(r => r != null)
                .OrderByDescending(r => r.PrivilegeLevel)
                .ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                .Select(r => new PermissionRoleSummary
                {
                    Code = r.Code,
                    Name = r.Name ?? r.Code,
                    Description = r.Description ?? "",
                    PrivilegeLevel = r.PrivilegeLevel,
                    Privileges = (r.Privileges ?? new List<string>())
                        .Concat(r.RuntimePrivileges ?? new HashSet<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                }).ToList(),
            Commands = new List<PlayerCommandStatus>
            {
                BuildCommandStatus(
                    "spawn",
                    "/spawn",
                    IsCommandEnabled("spawn", config.SpawnEnabled),
                    "/nse spawn",
                    "Teleport to the authoritative global world spawn."),
                BuildCommandStatus(
                    "msg",
                    "/msg <player> <message>",
                    IsCommandEnabled("msg", config.PrivateMessagesEnabled),
                    "/nse msg <player> <message>",
                    "Send a private message to an online player."),
                BuildCommandStatus(
                    "reply",
                    "/r <message>",
                    IsCommandEnabled("reply", config.PrivateMessagesEnabled && config.ReplyEnabled),
                    "/nse r <message>",
                    "Reply to the last private-message correspondent."),
                BuildStarterKitCommandStatus(),
                BuildCommandStatus(
                    "warp",
                    "/warp <name>",
                    IsCommandEnabled("warp", config.WarpsEnabled && config.WarpCommandEnabled),
                    "/nse warp <name>",
                    "Teleport to a configured server warp. Optional per-warp item costs, cooldowns and warm-up rules are supported."),
                BuildCommandStatus(
                    "back",
                    "/back",
                    IsCommandEnabled("back", config.BackEnabled),
                    "/nse back",
                    "Return to the most recent recorded death or Suite teleport origin."),
                BuildCommandStatus(
                    "home",
                    "/home [name]",
                    IsCommandEnabled("home", config.HomesEnabled),
                    "/nse home [name]",
                    "Teleport to a saved home."),
                BuildCommandStatus(
                    "sethome",
                    "/sethome [name]",
                    IsCommandEnabled("sethome", config.HomesEnabled),
                    "/nse sethome [name]",
                    "Create or move a home within your configured allowance."),
                BuildCommandStatus(
                    "delhome",
                    "/delhome [name]",
                    IsCommandEnabled("delhome", config.HomesEnabled),
                    "/nse delhome [name]",
                    "Delete a saved home."),
                BuildCommandStatus(
                    "homes",
                    "/homes",
                    IsCommandEnabled("homes", config.HomesEnabled),
                    "/nse homes",
                    "List saved homes and the effective home limit."),
                BuildCommandStatus(
                    "t2p",
                    "/t2p <player>",
                    IsCommandEnabled("t2p", config.T2pEnabled),
                    "/nse t2p <player>",
                    "Teleport yourself to an online player. Access and cooldowns are role-configurable; roles without an explicit policy require controlserver/root by default."),
                BuildCommandStatus(
                    "t2m",
                    "/t2m <player>",
                    IsCommandEnabled("t2m", config.T2mEnabled),
                    "/nse t2m <player>",
                    "Send a teleport request to an online player. They must use /tpaccept, then wait 10 seconds before being moved to you. Access and cooldowns are role-configurable; roles without an explicit policy require controlserver/root by default."),
                BuildCommandStatus(
                    "rtp",
                    "/rtp",
                    IsCommandEnabled("rtp", config.RtpEnabled),
                    "/nse rtp",
                    "Teleport to a safe random overworld location within the configured radius.")
            }
        };
    }

    public PlayerCommandSaveResult SaveSettings(PlayerCommandSettingsRequest? request)
    {
        if (request == null)
            return new PlayerCommandSaveResult
            {
                Success = false,
                Message = "Player command settings are required.",
                Snapshot = GetSnapshot()
            };

        if(request.RolePolicyRevision!=null && request.RolePolicyRevision!=BlockLogger.Roles.RoleManagementService.PolicyRevision(config))
            return new PlayerCommandSaveResult {Success=false,Message="Shared role policies changed elsewhere. Reload settings before saving; your draft has been kept.",Snapshot=GetSnapshot()};
        config.SpawnEnabled = request.SpawnEnabled;
        config.SpawnCooldownSeconds = request.SpawnCooldownSeconds;
        config.SpawnWarmupSeconds = request.SpawnWarmupSeconds;
        config.SpawnCancelOnMove = request.SpawnCancelOnMove;
        config.PrivateMessagesEnabled = request.PrivateMessagesEnabled;
        config.ReplyEnabled = request.ReplyEnabled;
        config.RespectSuiteMutes = request.RespectSuiteMutes;
        config.MaxPrivateMessageLength = request.MaxPrivateMessageLength;
        config.StarterKitEnabled = request.StarterKitEnabled;
        config.StarterKitProvider = request.StarterKitProvider;
        config.WarpsEnabled = request.WarpsEnabled;
        config.WarpCommandEnabled = request.WarpCommandEnabled;
        config.WarpDefaultCooldownSeconds = request.WarpDefaultCooldownSeconds ?? config.WarpDefaultCooldownSeconds;
        config.WarpDefaultWarmupSeconds = request.WarpDefaultWarmupSeconds ?? config.WarpDefaultWarmupSeconds;
        config.WarpDefaultCancelOnMove = request.WarpDefaultCancelOnMove ?? config.WarpDefaultCancelOnMove;
        config.BackEnabled = request.BackEnabled;
        config.BackCooldownSeconds = request.BackCooldownSeconds;
        config.BackWarmupSeconds = request.BackWarmupSeconds;
        config.BackCancelOnMove = request.BackCancelOnMove;
        config.BackTrackDeaths = request.BackTrackDeaths;
        config.BackTrackTeleports = request.BackTrackTeleports;
        config.HomesEnabled = request.HomesEnabled;
        config.HomeGlobalLimit = request.HomeGlobalLimit;
        config.HomeRoleLimits = request.HomeRoleLimits ?? config.HomeRoleLimits;
        config.HomePlayerLimits = NormalizePlayerLimitKeys(request.HomePlayerLimits ?? new Dictionary<string, int>());
        config.HomeCooldownSeconds = request.HomeCooldownSeconds;
        config.HomeWarmupSeconds = request.HomeWarmupSeconds;
        config.HomeCancelOnMove = request.HomeCancelOnMove;
        config.T2pEnabled = request.T2pEnabled;
        config.T2pCooldownSeconds = request.T2pCooldownSeconds;
        config.T2mEnabled = request.T2mEnabled;
        config.T2mCooldownSeconds = request.T2mCooldownSeconds;
        config.RtpEnabled = request.RtpEnabled;
        config.RtpMinRadius = request.RtpMinRadius;
        config.RtpMaxRadius = request.RtpMaxRadius;
        config.RtpCooldownSeconds = request.RtpCooldownSeconds;
        config.RtpWarmupSeconds = request.RtpWarmupSeconds;
        config.RtpCancelOnMove = request.RtpCancelOnMove;
        config.RtpCostEnabled = request.RtpCostEnabled;
        config.RtpCostItemCode = request.RtpCostItemCode;
        config.RtpCostQuantity = request.RtpCostQuantity;
        config.RtpMaxSearchAttempts = request.RtpMaxSearchAttempts;
        config.RtpAvoidWaterAndLava = request.RtpAvoidWaterAndLava;
        config.RtpCenterOnPlayerPosition = request.RtpCenterOnPlayerPosition;
        config.TeleportRolePolicies = request.TeleportRolePolicies ?? config.TeleportRolePolicies;
        config.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, config, PlayerCommandConfig.ConfigFile);

        api.Logger.Notification(
            "[ServerEssentials/PlayerCommands] Runtime settings updated. spawn={0}, msg={1}, reply={2}, cooldown={3}s, respectMutes={4}, maxPmLength={5}, starterkit={6}, starterKitProvider={7}, warps={8}, warpCommand={9}.",
            config.SpawnEnabled,
            config.PrivateMessagesEnabled,
            config.ReplyEnabled,
            config.SpawnCooldownSeconds,
            config.RespectSuiteMutes,
            config.MaxPrivateMessageLength,
            config.StarterKitEnabled,
            StarterKitEffectiveProvider,
            config.WarpsEnabled,
            config.WarpCommandEnabled);

        return new PlayerCommandSaveResult
        {
            Success = true,
            Message = "Player command settings saved and applied live.",
            Snapshot = GetSnapshot()
        };
    }

    public PermissionsManagerSnapshot GetPermissionsSnapshot()
    {
        var roles = (api.Server.Config.Roles ?? new List<IPlayerRole>())
            .Where(r => r != null)
            .OrderByDescending(r => r.PrivilegeLevel)
            .ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PermissionRoleSummary
            {
                Code = r.Code,
                Name = r.Name ?? r.Code,
                Description = r.Description ?? "",
                PrivilegeLevel = r.PrivilegeLevel,
                Privileges = (r.Privileges ?? new List<string>()).Concat(r.RuntimePrivileges ?? new HashSet<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            }).ToList();
        var known = Privilege.AllCodes().Concat(roles.SelectMany(r => r.Privileges)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var players = api.World.AllOnlinePlayers.OfType<IServerPlayer>().OrderBy(x => x.PlayerName, StringComparer.OrdinalIgnoreCase).Select(x => new PermissionPlayerSummary
        {
            PlayerUid = x.PlayerUID, PlayerName = x.PlayerName, RoleCode = x.ServerData?.RoleCode ?? x.Role?.Code ?? "",
            EffectivePrivileges = (x.Privileges ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            PermanentPrivileges = (x.ServerData?.PermaPrivileges ?? new HashSet<string>()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            DeniedPrivileges = (x.ServerData?.DeniedPrivileges ?? new HashSet<string>()).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList()
        }).ToList();
        return new PermissionsManagerSnapshot { Roles = roles, OnlinePlayers = players, KnownPrivileges = known };
    }

    public PermissionChangeResult ChangePermission(PermissionChangeRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlayerUid)) return new PermissionChangeResult { Success=false, Message="Player is required.", Snapshot=GetPermissionsSnapshot() };
        var player = OnlinePlayerByUid(request.PlayerUid.Trim());
        if (player == null) return new PermissionChangeResult { Success=false, Message="Player must be online to change roles or direct privileges safely.", Snapshot=GetPermissionsSnapshot() };
        string action=(request.Action??"").Trim().ToLowerInvariant(), value=(request.Value??"").Trim();
        bool ok=true; string message;
        switch(action)
        {
            case "set-role":
                if (api.Permissions.GetRole(value) == null) { ok=false; message="Unknown server role."; break; }
                api.Permissions.SetRole(player, value); message=$"Set {player.PlayerName} role to {value}."; break;
            case "grant":
                ok=api.Permissions.GrantPrivilege(player.PlayerUID, value, true); message=ok?$"Granted {value} permanently to {player.PlayerName}.":"Privilege grant failed."; break;
            case "revoke":
                ok=api.Permissions.RevokePrivilege(player.PlayerUID, value, true); message=ok?$"Revoked direct permanent privilege {value} from {player.PlayerName}.":"Privilege revoke failed."; break;
            case "deny":
                ok=api.Permissions.DenyPrivilege(player.PlayerUID, value); message=ok?$"Denied {value} for {player.PlayerName}.":"Privilege denial failed."; break;
            case "undeny":
                ok=api.Permissions.RemovePrivilegeDenial(player.PlayerUID, value); message=ok?$"Removed denial for {value} from {player.PlayerName}.":"Privilege denial removal failed."; break;
            default: ok=false; message="Unknown permission action."; break;
        }
        return new PermissionChangeResult { Success=ok, Message=message, Snapshot=GetPermissionsSnapshot() };
    }

    public void HandleSpawn(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("spawn", config.SpawnEnabled))
        {
            Reply(player, groupId, "The Server Essentials /spawn command is disabled.", false);
            return;
        }
        if (getAdmin()?.IsJailed(player.PlayerUID) == true)
        {
            Reply(player, groupId, "You cannot use /spawn while jailed.", false);
            return;
        }
        teleports.HandleSpawn(player, groupId, args);
    }


    public void HandleStarterKit(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("starterkit", config.StarterKitEnabled))
        {
            Reply(player, groupId, "The Server Essentials starter kit is disabled.", false);
            return;
        }
        if (!UsesNseStarterKit)
        {
            Reply(player, groupId, "Starter kits are delegated to Th3Essentials. Use its /starterkit command instead.", false);
            return;
        }
        if (starterKit.Items.Count == 0)
        {
            Reply(player, groupId, "No NSE starter kit has been configured yet. An admin can fill their hotbar and run /setstarterkit.", false);
            return;
        }
        if (starterKitClaims.Contains(player.PlayerUID))
        {
            Reply(player, groupId, "You have already claimed the starter kit.", false);
            return;
        }

        var hotbar = player.InventoryManager.GetHotbarInventory();
        if (hotbar == null)
        {
            Reply(player, groupId, "Your hotbar inventory is unavailable.", false);
            return;
        }

        var decoded = new List<(int Slot, ItemStack Stack)>();
        foreach (var item in starterKit.Items.OrderBy(x => x.Slot))
        {
            if (item.Slot < 0 || item.Slot >= hotbar.Count)
            {
                Reply(player, groupId, $"Starter kit slot {item.Slot + 1} is not available for your hotbar.", false);
                return;
            }
            var target = hotbar[item.Slot];
            if (target == null || target.Itemstack != null)
            {
                Reply(player, groupId, $"Clear hotbar slot {item.Slot + 1} before claiming the starter kit. The kit is placed atomically into its configured hotbar slots.", false);
                return;
            }

            ItemStack stack;
            try
            {
                stack = new ItemStack(Convert.FromBase64String(item.StackBase64));
                if (!stack.ResolveBlockOrItem(api.World))
                {
                    Reply(player, groupId, $"Starter kit item '{item.Code}' is no longer available on this server.", false);
                    return;
                }
            }
            catch
            {
                Reply(player, groupId, $"Starter kit item in slot {item.Slot + 1} could not be loaded. Ask an admin to run /setstarterkit again.", false);
                return;
            }

            if (!target.CanHold(new DummySlot(stack)))
            {
                Reply(player, groupId, $"Hotbar slot {item.Slot + 1} cannot accept '{item.Code}'.", false);
                return;
            }
            decoded.Add((item.Slot, stack));
        }

        var applied = new List<int>();
        try
        {
            foreach (var entry in decoded)
            {
                hotbar[entry.Slot]!.Itemstack = entry.Stack;
                hotbar[entry.Slot]!.MarkDirty();
                applied.Add(entry.Slot);
            }
            player.InventoryManager.BroadcastHotbarSlot();
            starterKitClaims.Add(player.PlayerUID);
            SaveStarterKitUsage();
            Reply(player, groupId, $"Starter kit claimed ({decoded.Count} occupied hotbar slot(s)).", true);
        }
        catch (Exception ex)
        {
            foreach (int slotId in applied)
            {
                try { hotbar[slotId]!.Itemstack = null; hotbar[slotId]!.MarkDirty(); } catch { }
            }
            api.Logger.Error("[ServerEssentials/PlayerCommands] Starter kit claim failed for {0}: {1}", player.PlayerName, ex);
            Reply(player, groupId, "The starter kit could not be applied; no claim was recorded. Check the server log.", false);
        }
    }

    public void HandleSetStarterKit(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!UsesNseStarterKit)
        {
            Reply(player, groupId, "Starter kits are delegated to Th3Essentials. Use Th3Essentials /setstarterkit instead, or change the NSE Starter Kit Provider to NSE.", false);
            return;
        }

        var hotbar = player.InventoryManager.GetHotbarInventory();
        if (hotbar == null)
        {
            Reply(player, groupId, "Your hotbar inventory is unavailable.", false);
            return;
        }

        var items = new List<StarterKitItemDefinition>();
        for (int i = 0; i < hotbar.Count; i++)
        {
            var stack = hotbar[i]?.Itemstack;
            if (stack == null) continue;
            try
            {
                items.Add(new StarterKitItemDefinition
                {
                    Slot = i,
                    StackBase64 = Convert.ToBase64String(stack.ToBytes()),
                    Code = stack.Collectible?.Code?.ToString() ?? "unknown",
                    StackSize = stack.StackSize
                });
            }
            catch (Exception ex)
            {
                Reply(player, groupId, $"Could not serialize hotbar slot {i + 1}: {ex.Message}", false);
                return;
            }
        }

        if (items.Count == 0)
        {
            Reply(player, groupId, "Your hotbar is empty. Put the desired starter-kit items in your hotbar, then run /setstarterkit.", false);
            return;
        }

        starterKit = new StarterKitDefinition
        {
            SchemaVersion = StarterKitDefinition.CurrentSchemaVersion,
            UpdatedBy = player.PlayerName,
            UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Items = items
        };
        BlockLogger.Services.SuiteConfigStorage.Store(api, starterKit, StarterKitDefinition.ConfigFile);
        Reply(player, groupId, $"NSE starter kit saved from {items.Count} occupied hotbar slot(s). Existing claim history was not reset.", true);
    }

    public void HandleResetStarterKitUsage(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!UsesNseStarterKit)
        {
            Reply(player, groupId, "Starter-kit usage is delegated to Th3Essentials.", false);
            return;
        }
        string targetName = (args.PopWord() ?? "").Trim();
        if (targetName.Length == 0)
        {
            Reply(player, groupId, "Usage: /resetstarterkitusage <online player>", false);
            return;
        }
        var target = OnlinePlayerByName(targetName);
        if (target == null)
        {
            Reply(player, groupId, "That player must be online so their permanent PlayerUID can be resolved safely.", false);
            return;
        }
        bool removed = starterKitClaims.Remove(target.PlayerUID);
        if (removed) SaveStarterKitUsage();
        Reply(player, groupId, removed ? $"Starter-kit usage reset for {target.PlayerName}." : $"{target.PlayerName} had not claimed the NSE starter kit.", true);
    }

    public void HandleResetStarterKitUsageAll(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!UsesNseStarterKit)
        {
            Reply(player, groupId, "Starter-kit usage is delegated to Th3Essentials.", false);
            return;
        }
        int count = starterKitClaims.Count;
        starterKitClaims.Clear();
        SaveStarterKitUsage();
        Reply(player, groupId, $"Starter-kit usage reset for all players ({count} recorded claim(s) cleared).", true);
    }

    public void HandleMsg(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("msg", config.PrivateMessagesEnabled))
        {
            Reply(player, groupId, "Private messages are disabled by Server Essentials.", false);
            return;
        }

        string targetName = (args.PopWord() ?? "").Trim();
        string message = (args.PopAll() ?? "").Trim();
        if (targetName.Length == 0 || message.Length == 0)
        {
            Reply(player, groupId, "Usage: /msg <player> <message>", false);
            return;
        }

        SendPrivateMessage(player, groupId, targetName, message);
    }

    public void HandleReply(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("reply", config.PrivateMessagesEnabled && config.ReplyEnabled))
        {
            Reply(player, groupId, "Private-message replies are disabled by Server Essentials.", false);
            return;
        }

        string message = (args.PopAll() ?? "").Trim();
        if (message.Length == 0)
        {
            Reply(player, groupId, "Usage: /r <message>", false);
            return;
        }

        if (!lastCorrespondentByUid.TryGetValue(player.PlayerUID, out string? targetUid) || string.IsNullOrWhiteSpace(targetUid))
        {
            Reply(player, groupId, "You do not have anyone to reply to yet.", false);
            return;
        }

        var target = OnlinePlayerByUid(targetUid);
        if (target == null)
        {
            Reply(player, groupId, "Your last private-message correspondent is no longer online.", false);
            return;
        }

        SendPrivateMessage(player, groupId, target.PlayerName, message);
    }

    public void HandleBack(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("back", config.BackEnabled)) { Reply(player, groupId, "The Server Essentials /back command is disabled.", false); return; }
        if (warps.HasPending(player.PlayerUID)) { Reply(player, groupId, "You already have a warp in progress.", false); return; }
        teleports.HandleBack(player, groupId, args);
    }
    public void HandleHome(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("home", config.HomesEnabled)) { Reply(player, groupId, "The Server Essentials /home command is disabled.", false); return; }
        if (warps.HasPending(player.PlayerUID)) { Reply(player, groupId, "You already have a warp in progress.", false); return; }
        teleports.HandleHome(player, groupId, args);
    }
    public void HandleSetHome(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("sethome", config.HomesEnabled)) { Reply(player, groupId, "The Server Essentials /sethome command is disabled.", false); return; }
        teleports.HandleSetHome(player, groupId, args);
    }
    public void HandleDeleteHome(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("delhome", config.HomesEnabled)) { Reply(player, groupId, "The Server Essentials /delhome command is disabled.", false); return; }
        teleports.HandleDeleteHome(player, groupId, args);
    }
    public void HandleHomes(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("homes", config.HomesEnabled)) { Reply(player, groupId, "The Server Essentials /homes command is disabled.", false); return; }
        teleports.HandleHomes(player, groupId, args);
    }
    public void HandleT2p(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("t2p", config.T2pEnabled)) { Reply(player, groupId, "The Server Essentials /t2p command is disabled.", false); return; }
        if (warps.HasPending(player.PlayerUID)) { Reply(player, groupId, "You already have a warp in progress.", false); return; }
        teleports.HandleT2p(player, groupId, args);
    }
    public void HandleT2m(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("t2m", config.T2mEnabled)) { Reply(player, groupId, "The Server Essentials /t2m command is disabled.", false); return; }
        teleports.HandleT2m(player, groupId, args);
    }
    public void HandleTpAccept(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("t2m", config.T2mEnabled)) { Reply(player, groupId, "The Server Essentials /t2m command is disabled.", false); return; }
        teleports.HandleTpAccept(player, groupId, args);
    }
    public void HandleRtp(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("rtp", config.RtpEnabled)) { Reply(player, groupId, "The Server Essentials /rtp command is disabled.", false); return; }
        if (warps.HasPending(player.PlayerUID)) { Reply(player, groupId, "You already have a warp in progress.", false); return; }
        teleports.HandleRtp(player, groupId, args);
    }

    public void HandleWarp(IServerPlayer player, int groupId, CmdArgs args)
    {
        if (!IsCommandEnabled("warp", config.WarpsEnabled && config.WarpCommandEnabled)) { Reply(player, groupId, "The Server Essentials /warp command is disabled.", false); return; }
        if (teleports.HasPending(player.PlayerUID)) { Reply(player, groupId, "You already have another teleport in progress.", false); return; }
        warps.HandleWarp(player, groupId, args);
    }
    public void HandleWarps(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleWarps(player, groupId, args);
    public void HandleWarpInfo(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleWarpInfo(player, groupId, args);
    public void HandleSetWarp(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleSetWarp(player, groupId, args);
    public void HandleDeleteWarp(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleDeleteWarp(player, groupId, args);
    public void HandleSetWarpCost(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleSetWarpCost(player, groupId, args);
    public void HandleSetWarpCooldown(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleSetWarpCooldown(player, groupId, args);
    public void HandleSetWarpWarmup(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleSetWarpWarmup(player, groupId, args);
    public void HandleSetWarpMovement(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleSetWarpMovement(player, groupId, args);
    public void HandleReloadWarps(IServerPlayer player, int groupId, CmdArgs args) => warps.HandleReloadWarps(player, groupId, args);

    private Dictionary<string, int> NormalizePlayerLimitKeys(Dictionary<string, int> input)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in input)
        {
            string key = (entry.Key ?? "").Trim();
            if (key.Length == 0) continue;
            var known = getAdmin()?.GetPlayer(key);
            result[known?.PlayerUid ?? key] = entry.Value;
        }
        return result;
    }

    private void SendPrivateMessage(IServerPlayer sender, int groupId, string targetName, string message)
    {
        if (config.RespectSuiteMutes && getAdmin()?.IsMuted(sender.PlayerUID, out var mute) == true)
        {
            string reason = string.IsNullOrWhiteSpace(mute?.MuteReason) ? "Muted by staff" : mute!.MuteReason;
            Reply(sender, groupId, $"You are muted and cannot send private messages. Reason: {reason}", false);
            return;
        }

        if (message.Length > config.MaxPrivateMessageLength)
        {
            Reply(sender, groupId, $"Private messages are limited to {config.MaxPrivateMessageLength} characters.", false);
            return;
        }

        var target = OnlinePlayerByName(targetName);
        if (target == null)
        {
            Reply(sender, groupId, $"Player '{targetName}' is not online.", false);
            return;
        }

        if (string.Equals(sender.PlayerUID, target.PlayerUID, StringComparison.OrdinalIgnoreCase))
        {
            Reply(sender, groupId, "You cannot private-message yourself.", false);
            return;
        }

        lastCorrespondentByUid[sender.PlayerUID] = target.PlayerUID;
        lastCorrespondentByUid[target.PlayerUID] = sender.PlayerUID;

        target.SendMessage(GlobalConstants.GeneralChatGroup,
            $"[PM from {sender.PlayerName}] {message}", EnumChatType.Notification);
        sender.SendMessage(groupId,
            $"[PM to {target.PlayerName}] {message}", EnumChatType.CommandSuccess);
    }


    private PlayerCommandStatus BuildStarterKitCommandStatus()
    {
        bool th3 = HasTh3Essentials;
        bool suiteOwned = suiteOwnedRootCommands.Contains("starterkit");
        string provider;
        if (th3)
        {
            provider = StarterKitEffectiveProvider == "Th3Essentials"
                ? "Th3Essentials (selected provider)"
                : "Th3Essentials owns root; NSE available as /nse starterkit";
            suiteOwned = false;
        }
        else if (suiteOwned) provider = "Newfie's Server Essentials";
        else if (api.ChatCommands.Get("starterkit") != null) provider = "Another command provider";
        else provider = "Unregistered";

        return new PlayerCommandStatus
        {
            Key = "starterkit",
            Command = "/starterkit",
            Enabled = IsCommandEnabled("starterkit", config.StarterKitEnabled && UsesNseStarterKit),
            IndividuallyEnabled = IsIndividualCommandEnabled("starterkit"),
            RootProvider = provider,
            RootOwnedBySuite = suiteOwned,
            FallbackCommand = "/nse starterkit",
            Description = $"Claim the configured starter kit once. Effective provider: {StarterKitEffectiveProvider}.",
            RequiredPrivilege = Privilege.chat
        };
    }

    private void LoadStarterKitUsage()
    {
        starterKitClaims.Clear();
        try
        {
            byte[]? bytes = api.WorldManager.SaveGame.GetData(StarterKitUsageSaveKey);
            if (bytes == null || bytes.Length == 0) return;
            var data = JsonSerializer.Deserialize<StarterKitUsageDatabase>(Encoding.UTF8.GetString(bytes));
            foreach (string uid in data?.ClaimedPlayerUids ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(uid)) starterKitClaims.Add(uid);
        }
        catch (Exception ex)
        {
            api.Logger.Error("[ServerEssentials/PlayerCommands] Failed to load starter-kit usage database. Existing save data was left untouched. {0}", ex);
        }
    }

    private void SaveStarterKitUsage()
    {
        var data = new StarterKitUsageDatabase
        {
            ClaimedPlayerUids = starterKitClaims.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };
        api.WorldManager.SaveGame.StoreData(StarterKitUsageSaveKey, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data)));
    }

    private PlayerCommandStatus BuildCommandStatus(
        string key,
        string command,
        bool enabled,
        string fallback,
        string description)
    {
        string rootName = key == "reply" ? "r" : key;
        bool th3 = HasTh3Essentials;
        bool suiteOwned = suiteOwnedRootCommands.Contains(rootName);
        string provider;

        bool reservedForTh3 = th3 && (rootName.Equals("spawn", StringComparison.OrdinalIgnoreCase)
            || rootName.Equals("msg", StringComparison.OrdinalIgnoreCase)
            || rootName.Equals("r", StringComparison.OrdinalIgnoreCase));
        if (reservedForTh3)
        {
            provider = api.ChatCommands.Get(rootName) != null
                ? "External provider (Th3Essentials compatibility mode)"
                : "Reserved for Th3Essentials compatibility";
            suiteOwned = false;
        }
        else if (suiteOwned)
        {
            provider = "Newfie's Server Essentials";
        }
        else if (api.ChatCommands.Get(rootName) != null)
        {
            provider = "Another command provider";
        }
        else
        {
            provider = "Unregistered";
        }

        // IChatCommand in Vintage Story 1.22.7 does not expose its configured privilege
        // for readback. Every player-facing NSE root represented in this snapshot is
        // registered with the normal chat privilege; if another provider owns the root,
        // this is the privilege required by the always-available /nse fallback.
        string requiredPrivilege = key.Equals("t2p", StringComparison.OrdinalIgnoreCase) || key.Equals("t2m", StringComparison.OrdinalIgnoreCase)
            ? "role policy; default controlserver/root"
            : Privilege.chat;

        return new PlayerCommandStatus
        {
            Key = key,
            Command = command,
            Enabled = enabled,
            IndividuallyEnabled = IsIndividualCommandEnabled(key),
            RootProvider = provider,
            RootOwnedBySuite = suiteOwned,
            FallbackCommand = fallback,
            Description = description,
            RequiredPrivilege = requiredPrivilege
        };
    }

    private IServerPlayer? OnlinePlayerByName(string name) => api.World.AllOnlinePlayers
        .OfType<IServerPlayer>()
        .FirstOrDefault(p => string.Equals(p.PlayerName, name, StringComparison.OrdinalIgnoreCase));

    private IServerPlayer? OnlinePlayerByUid(string uid) => api.World.AllOnlinePlayers
        .OfType<IServerPlayer>()
        .FirstOrDefault(p => string.Equals(p.PlayerUID, uid, StringComparison.OrdinalIgnoreCase));

    private void SafeRegisterRoot(
        string command,
        string description,
        string syntax,
        ServerChatCommandDelegate handler,
        string requiredPrivilege)
    {
        try
        {
            if (api.ChatCommands.Get(command) != null)
            {
                api.Logger.Notification(
                    "[ServerEssentials/PlayerCommands] Root /{0} is already owned by another provider. NSE left it untouched.",
                    command);
                return;
            }

#pragma warning disable CS0618
            api.RegisterCommand(command, description, syntax, handler, requiredPrivilege);
#pragma warning restore CS0618
            suiteOwnedRootCommands.Add(command);
        }
        catch (Exception ex)
        {
            api.Logger.Warning(
                "[ServerEssentials/PlayerCommands] Could not register root /{0}; leaving any existing provider untouched. {1}: {2}",
                command, ex.GetType().Name, ex.Message);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        lastCorrespondentByUid.Remove(player.PlayerUID);
        foreach (string uid in lastCorrespondentByUid
                     .Where(x => string.Equals(x.Value, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Key)
                     .ToArray())
        {
            lastCorrespondentByUid.Remove(uid);
        }
    }

    private static void Reply(IServerPlayer player, int groupId, string message, bool success) =>
        player.SendMessage(groupId, message, success ? EnumChatType.CommandSuccess : EnumChatType.CommandError);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        api.Event.PlayerDisconnect -= OnPlayerDisconnect;
        lastCorrespondentByUid.Clear();
        suiteOwnedRootCommands.Clear();
        warps.Dispose();
        teleports.Dispose();
    }
}
