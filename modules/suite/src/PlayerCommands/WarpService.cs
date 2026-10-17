using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BlockLogger.AntiCheat;
using BlockLogger.Services;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace BlockLogger.PlayerCommands;

public sealed class WarpDefinition
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    // -1 = inherit the Player Commands defaults.
    public int CooldownSeconds { get; set; } = -1;
    public int WarmupSeconds { get; set; } = -1;
    public bool? CancelOnMove { get; set; }

    public bool CostEnabled { get; set; }
    public string CostItemCode { get; set; } = "";
    public int CostQuantity { get; set; } = 1;

    // Optional access control. Public when both lists are empty.
    // any = one matching role OR one matching privilege grants access.
    // all = one matching role (when roles are configured) AND every configured privilege.
    public string AccessMode { get; set; } = "any";
    public List<string> AllowedRoles { get; set; } = new();
    public List<string> RequiredPrivileges { get; set; } = new();

    private static List<string> NormalizeAccessList(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
        .Select(v => (v ?? "").Trim())
        .Where(v => v.Length > 0)
        .Select(v => v.Length > 64 ? v[..64] : v)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public void Clamp()
    {
        Name = WarpService.NormalizeWarpName(Name);
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName.Trim();
        if (DisplayName.Length > 64) DisplayName = DisplayName[..64];
        CooldownSeconds = Math.Clamp(CooldownSeconds, -1, 86_400);
        WarmupSeconds = Math.Clamp(WarmupSeconds, -1, 300);
        CostItemCode = (CostItemCode ?? "").Trim();
        CostQuantity = Math.Clamp(CostQuantity, 1, 65_535);
        if (string.IsNullOrWhiteSpace(CostItemCode)) CostEnabled = false;
        AccessMode = string.Equals(AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "any";
        AllowedRoles = NormalizeAccessList(AllowedRoles);
        RequiredPrivileges = NormalizeAccessList(RequiredPrivileges);
    }
}

public sealed class WarpDatabase
{
    public const string ConfigFile = "NewfiesWarps.json";
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<WarpDefinition> Warps { get; set; } = new();

    public void Clamp()
    {
        SchemaVersion = Math.Max(CurrentSchemaVersion, SchemaVersion);
        Warps ??= new List<WarpDefinition>();
        var unique = new Dictionary<string, WarpDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var warp in Warps.Where(x => x != null))
        {
            warp.Clamp();
            if (string.IsNullOrWhiteSpace(warp.Name)) continue;
            unique[warp.Name] = warp;
        }
        Warps = unique.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static WarpDatabase Load(ICoreServerAPI api)
    {
        var db = ModConfigRecovery.LoadOrNull<WarpDatabase>(api, ConfigFile, "Warps") ?? new WarpDatabase();
        db.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, db, ConfigFile);
        return db;
    }
}

public sealed class WarpSnapshot
{
    public bool Available { get; set; } = true;
    public bool Enabled { get; set; }
    public int DefaultCooldownSeconds { get; set; }
    public int DefaultWarmupSeconds { get; set; }
    public bool DefaultCancelOnMove { get; set; }
    public List<WarpDefinition> Warps { get; set; } = new();
    public List<string> AvailableRoles { get; set; } = new();
    public List<string> AvailablePrivileges { get; set; } = new();
}

public sealed class WarpSettingsRequest
{
    public bool Enabled { get; set; } = true;
    public int DefaultCooldownSeconds { get; set; } = 60;
    public int DefaultWarmupSeconds { get; set; } = 5;
    public bool DefaultCancelOnMove { get; set; } = true;
}

public sealed class WarpMutationRequest
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public int CooldownSeconds { get; set; } = -1;
    public int WarmupSeconds { get; set; } = -1;
    public bool? CancelOnMove { get; set; }
    public bool CostEnabled { get; set; }
    public string CostItemCode { get; set; } = "";
    public int CostQuantity { get; set; } = 1;

    // Optional access control. Public when both lists are empty.
    // any = one matching role OR one matching privilege grants access.
    // all = one matching role (when roles are configured) AND every configured privilege.
    public string AccessMode { get; set; } = "any";
    public List<string> AllowedRoles { get; set; } = new();
    public List<string> RequiredPrivileges { get; set; } = new();
}

public sealed class WarpMutationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public WarpSnapshot Snapshot { get; set; } = new();
}

public sealed class WarpService : IDisposable
{
    private sealed class PendingWarp
    {
        public string PlayerUid { get; init; } = "";
        public string WarpName { get; init; } = "";
        public long DueUtcMs { get; init; }
        public double StartX { get; init; }
        public double StartY { get; init; }
        public double StartZ { get; init; }
        public bool CancelOnMove { get; init; }
    }

    private readonly ICoreServerAPI api;
    private readonly Func<PlayerCommandConfig> getConfig;
    private readonly Action<IServerPlayer, EntityPos?, string>? recordTeleportOrigin;
    private readonly System.Func<string, bool>? isJailed;
    private WarpDatabase database;
    private readonly Dictionary<string, long> lastWarpUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingWarp> pendingByUid = new(StringComparer.OrdinalIgnoreCase);
    private long tickListenerId;
    private bool disposed;

    public WarpService(ICoreServerAPI api, Func<PlayerCommandConfig> getConfig, Action<IServerPlayer, EntityPos?, string>? recordTeleportOrigin = null, System.Func<string, bool>? isJailed = null)
    {
        this.api = api;
        this.getConfig = getConfig;
        this.recordTeleportOrigin = recordTeleportOrigin;
        this.isJailed = isJailed;
        database = WarpDatabase.Load(api);
    }

    public bool HasPending(string playerUid) => !string.IsNullOrWhiteSpace(playerUid) && pendingByUid.ContainsKey(playerUid);

    public void Start()
    {
        tickListenerId = api.Event.RegisterGameTickListener(_ => Tick(), 250, 250);
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
    }

    public WarpSnapshot GetSnapshot()
    {
        var cfg = getConfig();
        return new WarpSnapshot
        {
            Available = true,
            Enabled = cfg.WarpsEnabled,
            DefaultCooldownSeconds = cfg.WarpDefaultCooldownSeconds,
            DefaultWarmupSeconds = cfg.WarpDefaultWarmupSeconds,
            DefaultCancelOnMove = cfg.WarpDefaultCancelOnMove,
            Warps = database.Warps.Select(Clone).ToList(),
            AvailableRoles = GetAvailableRoles(),
            AvailablePrivileges = GetAvailablePrivileges()
        };
    }

    public WarpMutationResult SaveDefaults(WarpSettingsRequest? request)
    {
        if (request == null) return Result(false, "Warp settings are required.");
        var cfg = getConfig();
        cfg.WarpsEnabled = request.Enabled;
        cfg.WarpDefaultCooldownSeconds = request.DefaultCooldownSeconds;
        cfg.WarpDefaultWarmupSeconds = request.DefaultWarmupSeconds;
        cfg.WarpDefaultCancelOnMove = request.DefaultCancelOnMove;
        cfg.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, cfg, PlayerCommandConfig.ConfigFile);
        return Result(true, "Warp defaults saved and applied live.");
    }

    public WarpMutationResult Upsert(WarpMutationRequest? request)
    {
        if (request == null) return Result(false, "Warp data is required.");
        string name = NormalizeWarpName(request.Name);
        if (string.IsNullOrWhiteSpace(name)) return Result(false, "Warp name must contain letters, numbers, '-' or '_'.");

        var warp = database.Warps.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (warp == null)
        {
            warp = new WarpDefinition { Name = name };
            database.Warps.Add(warp);
        }

        warp.DisplayName = request.DisplayName;
        warp.Enabled = request.Enabled;
        warp.X = request.X;
        warp.Y = request.Y;
        warp.Z = request.Z;
        warp.CooldownSeconds = request.CooldownSeconds;
        warp.WarmupSeconds = request.WarmupSeconds;
        warp.CancelOnMove = request.CancelOnMove;
        warp.CostEnabled = request.CostEnabled;
        warp.CostItemCode = request.CostItemCode;
        warp.CostQuantity = request.CostQuantity;
        warp.AccessMode = request.AccessMode;
        warp.AllowedRoles = request.AllowedRoles ?? new List<string>();
        warp.RequiredPrivileges = request.RequiredPrivileges ?? new List<string>();
        warp.Clamp();
        SaveDatabase();
        return Result(true, $"Warp '{warp.Name}' saved.");
    }

    public WarpMutationResult Delete(string? name)
    {
        name = NormalizeWarpName(name);
        int removed = database.Warps.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return Result(false, $"Warp '{name}' was not found.");
        SaveDatabase();
        return Result(true, $"Warp '{name}' deleted.");
    }

    public WarpMutationResult Reload()
    {
        database = WarpDatabase.Load(api);
        return Result(true, $"Reloaded {database.Warps.Count} warp(s) from {WarpDatabase.ConfigFile}.");
    }

    public void HandleWarp(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (isJailed?.Invoke(player.PlayerUID) == true)
        {
            Reply(player, groupId, "You cannot use /warp while jailed.", false);
            return;
        }
        if (!cfg.WarpsEnabled)
        {
            Reply(player, groupId, "Warps are disabled by Server Essentials.", false);
            return;
        }
        if (!cfg.WarpCommandEnabled)
        {
            Reply(player, groupId, "The Server Essentials /warp command is disabled.", false);
            return;
        }

        // Apply the role-level command gate before listing or resolving a named warp.
        // Per-warp access restrictions are still evaluated separately below.
        var commandRolePolicy = cfg.ResolveTeleportRolePolicy(
            "warp",
            player.ServerData?.RoleCode ?? player.Role?.Code ?? "",
            cfg.WarpDefaultCooldownSeconds);
        if (!commandRolePolicy.Allowed)
        {
            string role = string.IsNullOrWhiteSpace(commandRolePolicy.RoleCode) ? "your role" : $"role '{commandRolePolicy.RoleCode}'";
            Reply(player, groupId, $"/warp is not available to {role}.", false);
            return;
        }

        string name = NormalizeWarpName(args.PopWord());
        if (string.IsNullOrWhiteSpace(name))
        {
            HandleWarps(player, groupId, args);
            return;
        }

        var warp = Find(name);
        if (warp == null || !warp.Enabled)
        {
            Reply(player, groupId, $"Warp '{name}' is not available.", false);
            return;
        }
        if (!CanUseWarp(player, warp, out string accessReason))
        {
            Reply(player, groupId, $"You do not have access to warp {Display(warp)}. {accessReason}", false);
            return;
        }

        var rolePolicy = cfg.ResolveTeleportRolePolicy(
            "warp",
            player.ServerData?.RoleCode ?? player.Role?.Code ?? "",
            EffectiveCooldown(warp, cfg));
        if (!rolePolicy.Allowed)
        {
            string role = string.IsNullOrWhiteSpace(rolePolicy.RoleCode) ? "your role" : $"role '{rolePolicy.RoleCode}'";
            Reply(player, groupId, $"/warp is not available to {role}.", false);
            return;
        }

        if (pendingByUid.ContainsKey(player.PlayerUID))
        {
            Reply(player, groupId, "You already have a warp pending. Move to cancel it or wait for it to complete.", false);
            return;
        }

        int cooldown = rolePolicy.CooldownSeconds;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string cooldownKey = CooldownKey(player.PlayerUID, warp.Name);
        if (cooldown > 0 && lastWarpUtc.TryGetValue(cooldownKey, out long previous))
        {
            long remain = previous + cooldown * 1000L - now;
            if (remain > 0)
            {
                Reply(player, groupId, $"You can use warp {Display(warp)} again in {(int)Math.Ceiling(remain / 1000d)} second(s).", false);
                return;
            }
        }

        if (!HasCost(player, warp, out int have))
        {
            Reply(player, groupId, $"Warp {Display(warp)} requires {warp.CostQuantity} × {warp.CostItemCode}. You currently have {have}.", false);
            return;
        }

        int warmup = EffectiveWarmup(warp, cfg);
        bool cancelOnMove = warp.CancelOnMove ?? cfg.WarpDefaultCancelOnMove;
        if (warmup <= 0)
        {
            CompleteWarp(player, warp, groupId);
            return;
        }

        var startPos = EntityRuntimeCompat.GetPos(player.Entity);
        if (startPos == null)
        {
            Reply(player, groupId, "Warp could not read your current position. Try again in a moment.", false);
            return;
        }

        pendingByUid[player.PlayerUID] = new PendingWarp
        {
            PlayerUid = player.PlayerUID,
            WarpName = warp.Name,
            DueUtcMs = now + warmup * 1000L,
            StartX = startPos.X,
            StartY = startPos.Y,
            StartZ = startPos.Z,
            CancelOnMove = cancelOnMove
        };

        string cost = warp.CostEnabled ? $" Cost: {warp.CostQuantity} × {warp.CostItemCode}." : "";
        string movement = cancelOnMove ? " Do not move." : "";
        Reply(player, groupId, $"Teleporting to {Display(warp)} in {warmup} second(s).{cost}{movement}", true);
    }

    public void HandleWarps(IServerPlayer player, int groupId, CmdArgs args)
    {
        var cfg = getConfig();
        if (!cfg.WarpsEnabled)
        {
            Reply(player, groupId, "Warps are disabled by Server Essentials.", false);
            return;
        }
        var list = database.Warps.Where(x => x.Enabled && CanUseWarp(player, x, out _)).OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            Reply(player, groupId, "No warps are currently available to you.", true);
            return;
        }
        string text = string.Join(", ", list.Select(x => x.CostEnabled
            ? $"{Display(x)} ({x.CostQuantity}× {x.CostItemCode})"
            : Display(x)));
        Reply(player, groupId, "Available warps: " + text, true);
    }

    public void HandleWarpInfo(IServerPlayer player, int groupId, CmdArgs args)
    {
        string name = NormalizeWarpName(args.PopWord());
        if (string.IsNullOrWhiteSpace(name))
        {
            Reply(player, groupId, "Usage: /warpinfo <name>", false);
            return;
        }
        var warp = Find(name);
        if (warp == null)
        {
            Reply(player, groupId, $"Warp '{name}' was not found.", false);
            return;
        }
        if (!CanUseWarp(player, warp, out string accessReason))
        {
            Reply(player, groupId, $"You do not have access to warp {Display(warp)}. {accessReason}", false);
            return;
        }
        var cfg = getConfig();
        int baseCooldown = EffectiveCooldown(warp, cfg);
        var rolePolicy = cfg.ResolveTeleportRolePolicy(
            "warp",
            player.ServerData?.RoleCode ?? player.Role?.Code ?? "",
            baseCooldown);
        int cooldown = rolePolicy.CooldownSeconds;
        int warmup = EffectiveWarmup(warp, cfg);
        bool cancel = warp.CancelOnMove ?? cfg.WarpDefaultCancelOnMove;
        string cost = warp.CostEnabled ? $"{warp.CostQuantity} × {warp.CostItemCode}" : "Free";
        string access = AccessSummary(warp);
        Reply(player, groupId,
            $"Warp {Display(warp)} [{warp.Name}] — {(warp.Enabled ? "Enabled" : "Disabled")} — Position {warp.X:0.##}, {warp.Y:0.##}, {warp.Z:0.##} — Cooldown {cooldown}s — Warm-up {warmup}s — Cancel on move {(cancel ? "ON" : "OFF")} — Cost {cost} — Access {access}.",
            true);
    }

    public void HandleSetWarp(IServerPlayer player, int groupId, CmdArgs args)
    {
        string name = NormalizeWarpName(args.PopWord());
        if (string.IsNullOrWhiteSpace(name))
        {
            Reply(player, groupId, "Usage: /setwarp <name> [itemcode] [quantity]", false);
            return;
        }

        var existing = Find(name);
        string itemCode = (args.PopWord() ?? "").Trim();
        int quantity = 1;
        string qtyWord = (args.PopWord() ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(qtyWord) && (!int.TryParse(qtyWord, NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity) || quantity < 1))
        {
            Reply(player, groupId, "Warp cost quantity must be a positive whole number.", false);
            return;
        }

        var pos = EntityRuntimeCompat.GetPos(player.Entity);
        if (pos == null)
        {
            Reply(player, groupId, "Unable to read your current position, so the warp was not saved.", false);
            return;
        }

        var request = new WarpMutationRequest
        {
            Name = name,
            DisplayName = existing?.DisplayName ?? name,
            Enabled = existing?.Enabled ?? true,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
            CooldownSeconds = existing?.CooldownSeconds ?? -1,
            WarmupSeconds = existing?.WarmupSeconds ?? -1,
            CancelOnMove = existing?.CancelOnMove,
            CostEnabled = existing?.CostEnabled ?? false,
            CostItemCode = existing?.CostItemCode ?? "",
            CostQuantity = existing?.CostQuantity ?? 1,
            AccessMode = existing?.AccessMode ?? "any",
            AllowedRoles = existing?.AllowedRoles?.ToList() ?? new List<string>(),
            RequiredPrivileges = existing?.RequiredPrivileges?.ToList() ?? new List<string>()
        };
        if (!string.IsNullOrWhiteSpace(itemCode))
        {
            request.CostEnabled = !itemCode.Equals("none", StringComparison.OrdinalIgnoreCase);
            request.CostItemCode = request.CostEnabled ? itemCode : "";
            request.CostQuantity = quantity;
        }

        var result = Upsert(request);
        Reply(player, groupId, result.Success
            ? $"Warp '{name}' saved at {pos.X:0.##}, {pos.Y:0.##}, {pos.Z:0.##}."
            : result.Message, result.Success);
    }

    public void HandleDeleteWarp(IServerPlayer player, int groupId, CmdArgs args)
    {
        string name = NormalizeWarpName(args.PopWord());
        if (string.IsNullOrWhiteSpace(name))
        {
            Reply(player, groupId, "Usage: /delwarp <name>", false);
            return;
        }
        var result = Delete(name);
        Reply(player, groupId, result.Message, result.Success);
    }

    public void HandleSetWarpCost(IServerPlayer player, int groupId, CmdArgs args)
    {
        string name = NormalizeWarpName(args.PopWord());
        string item = (args.PopWord() ?? "").Trim();
        string qtyWord = (args.PopWord() ?? "1").Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(item))
        {
            Reply(player, groupId, "Usage: /setwarpcost <warp> <itemcode|none> [quantity]", false);
            return;
        }
        var warp = Find(name);
        if (warp == null) { Reply(player, groupId, $"Warp '{name}' was not found.", false); return; }
        if (!int.TryParse(qtyWord, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qty) || qty < 1)
        {
            Reply(player, groupId, "Quantity must be a positive whole number.", false);
            return;
        }
        warp.CostEnabled = !item.Equals("none", StringComparison.OrdinalIgnoreCase);
        warp.CostItemCode = warp.CostEnabled ? item : "";
        warp.CostQuantity = qty;
        warp.Clamp();
        SaveDatabase();
        Reply(player, groupId, warp.CostEnabled
            ? $"Warp '{name}' now costs {warp.CostQuantity} × {warp.CostItemCode}."
            : $"Warp '{name}' is now free.", true);
    }

    public void HandleSetWarpCooldown(IServerPlayer player, int groupId, CmdArgs args)
        => HandlePerWarpInt(player, groupId, args, "cooldown", 86_400, (w, v) => w.CooldownSeconds = v);

    public void HandleSetWarpWarmup(IServerPlayer player, int groupId, CmdArgs args)
        => HandlePerWarpInt(player, groupId, args, "warmup", 300, (w, v) => w.WarmupSeconds = v);

    public void HandleSetWarpMovement(IServerPlayer player, int groupId, CmdArgs args)
    {
        string name = NormalizeWarpName(args.PopWord());
        string value = (args.PopWord() ?? "").Trim().ToLowerInvariant();
        var warp = Find(name);
        if (warp == null) { Reply(player, groupId, $"Warp '{name}' was not found.", false); return; }
        warp.CancelOnMove = value switch
        {
            "on" or "true" or "yes" => true,
            "off" or "false" or "no" => false,
            "default" or "inherit" => null,
            _ => warp.CancelOnMove
        };
        if (value is not ("on" or "true" or "yes" or "off" or "false" or "no" or "default" or "inherit"))
        {
            Reply(player, groupId, "Usage: /setwarpmove <warp> <on|off|default>", false);
            return;
        }
        SaveDatabase();
        Reply(player, groupId, $"Warp '{name}' movement cancellation set to {(warp.CancelOnMove.HasValue ? (warp.CancelOnMove.Value ? "ON" : "OFF") : "DEFAULT")}.", true);
    }

    public void HandleReloadWarps(IServerPlayer player, int groupId, CmdArgs args)
    {
        var result = Reload();
        Reply(player, groupId, result.Message, result.Success);
    }

    private void HandlePerWarpInt(IServerPlayer player, int groupId, CmdArgs args, string label, int max, Action<WarpDefinition, int> setter)
    {
        string name = NormalizeWarpName(args.PopWord());
        string value = (args.PopWord() ?? "").Trim();
        var warp = Find(name);
        if (warp == null) { Reply(player, groupId, $"Warp '{name}' was not found.", false); return; }
        int parsed;
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase) || value.Equals("inherit", StringComparison.OrdinalIgnoreCase)) parsed = -1;
        else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < 0 || parsed > max)
        {
            Reply(player, groupId, $"Usage: /setwarp{label} <warp> <0-{max}|default>", false);
            return;
        }
        setter(warp, parsed);
        SaveDatabase();
        Reply(player, groupId, $"Warp '{name}' {label} set to {(parsed < 0 ? "DEFAULT" : parsed + " second(s)")}.", true);
    }

    private void Tick()
    {
        if (pendingByUid.Count == 0) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var pending in pendingByUid.Values.ToArray())
        {
            var player = OnlineByUid(pending.PlayerUid);
            if (player == null)
            {
                pendingByUid.Remove(pending.PlayerUid);
                continue;
            }
            if (isJailed?.Invoke(player.PlayerUID) == true)
            {
                pendingByUid.Remove(pending.PlayerUid);
                Reply(player, GlobalConstants.GeneralChatGroup, "Warp cancelled because you were jailed.", false);
                continue;
            }

            if (pending.CancelOnMove)
            {
                var currentPos = EntityRuntimeCompat.GetPos(player.Entity);
                if (currentPos == null)
                {
                    pendingByUid.Remove(pending.PlayerUid);
                    Reply(player, GlobalConstants.GeneralChatGroup, "Warp cancelled because your position could not be read safely.", false);
                    continue;
                }

                double dx = currentPos.X - pending.StartX;
                double dy = currentPos.Y - pending.StartY;
                double dz = currentPos.Z - pending.StartZ;
                if (dx * dx + dy * dy + dz * dz > 0.16)
                {
                    pendingByUid.Remove(pending.PlayerUid);
                    Reply(player, GlobalConstants.GeneralChatGroup, "Warp cancelled because you moved.", false);
                    continue;
                }
            }

            if (now < pending.DueUtcMs) continue;
            pendingByUid.Remove(pending.PlayerUid);
            var warp = Find(pending.WarpName);
            if (warp == null || !warp.Enabled)
            {
                Reply(player, GlobalConstants.GeneralChatGroup, "Warp cancelled because the destination is no longer available.", false);
                continue;
            }
            if (!CanUseWarp(player, warp, out _))
            {
                Reply(player, GlobalConstants.GeneralChatGroup, "Warp cancelled because you no longer have access to that destination.", false);
                continue;
            }
            CompleteWarp(player, warp, GlobalConstants.GeneralChatGroup);
        }
    }

    private void CompleteWarp(IServerPlayer player, WarpDefinition warp, int groupId)
    {
        if (!CanUseWarp(player, warp, out string accessReason))
        {
            Reply(player, groupId, $"Warp cancelled because you no longer have access. {accessReason}", false);
            return;
        }
        if (isJailed?.Invoke(player.PlayerUID) == true)
        {
            Reply(player, groupId, "Warp cancelled because you are jailed.", false);
            return;
        }
        var currentPos = EntityRuntimeCompat.GetPos(player.Entity);
        if (currentPos == null)
        {
            Reply(player, groupId, "The warp could not read your current position, so nothing was consumed.", false);
            return;
        }

        if (!HasCost(player, warp, out int have))
        {
            Reply(player, groupId, $"Warp {Display(warp)} requires {warp.CostQuantity} × {warp.CostItemCode}. You currently have {have}.", false);
            return;
        }

        ItemStack? refund = null;
        if (warp.CostEnabled && !TryRemoveCost(player, warp, out refund))
        {
            Reply(player, groupId, "Your inventory changed before the warp completed, so no items were consumed.", false);
            return;
        }

        try
        {
            var destination = currentPos.Copy();
            destination.X = warp.X;
            destination.Y = warp.Y;
            destination.Z = warp.Z;
            PacketGuardTrustedState.Mark(player, "warp", 5_000, PacketGuardTrustScope.All);
            player.Entity.TeleportTo(destination);
            recordTeleportOrigin?.Invoke(player, currentPos.Copy(), $"warp:{warp.Name}");
            lastWarpUtc[CooldownKey(player.PlayerUID, warp.Name)] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Reply(player, groupId, warp.CostEnabled
                ? $"Teleported to {Display(warp)}. Consumed {warp.CostQuantity} × {warp.CostItemCode}."
                : $"Teleported to {Display(warp)}.", true);
        }
        catch (Exception ex)
        {
            if (refund != null)
            {
                try { player.Entity.TryGiveItemStack(refund); } catch { }
            }
            api.Logger.Error("[ServerEssentials/Warps] Teleport to {0} failed for {1}: {2}", warp.Name, player.PlayerName, ex);
            Reply(player, groupId, "The warp failed. Any warp cost that was removed was refunded where inventory space allowed.", false);
        }
    }

    private bool HasCost(IServerPlayer player, WarpDefinition warp, out int count)
    {
        count = 0;
        if (!warp.CostEnabled || string.IsNullOrWhiteSpace(warp.CostItemCode) || warp.CostQuantity <= 0) return true;
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (inv == null) continue;
            for (int i = 0; i < inv.Count; i++)
            {
                var stack = inv[i]?.Itemstack;
                if (stack?.Collectible?.Code == null) continue;
                if (!stack.Collectible.Code.ToString().Equals(warp.CostItemCode, StringComparison.OrdinalIgnoreCase)) continue;
                count += Math.Max(0, stack.StackSize);
                if (count >= warp.CostQuantity) return true;
            }
        }
        return false;
    }

    private bool TryRemoveCost(IServerPlayer player, WarpDefinition warp, out ItemStack? refund)
    {
        refund = null;
        if (!warp.CostEnabled) return true;
        if (!HasCost(player, warp, out _)) return false;

        int remaining = warp.CostQuantity;
        CollectibleObject? collectible = null;
        foreach (var inv in player.InventoryManager.InventoriesOrdered)
        {
            if (inv == null) continue;
            for (int i = 0; i < inv.Count && remaining > 0; i++)
            {
                var slot = inv[i];
                var stack = slot?.Itemstack;
                if (slot == null || stack?.Collectible?.Code == null) continue;
                if (!stack.Collectible.Code.ToString().Equals(warp.CostItemCode, StringComparison.OrdinalIgnoreCase)) continue;
                collectible ??= stack.Collectible;
                int take = Math.Min(remaining, stack.StackSize);
                stack.StackSize -= take;
                remaining -= take;
                if (stack.StackSize <= 0) slot.Itemstack = null;
                slot.MarkDirty();
            }
            if (remaining <= 0) break;
        }

        if (remaining > 0 || collectible == null) return false;
        refund = new ItemStack(collectible, warp.CostQuantity);
        return true;
    }

    private bool CanUseWarp(IServerPlayer player, WarpDefinition warp, out string reason)
    {
        reason = "";
        if (player.HasPrivilege(Privilege.root) || player.HasPrivilege(Privilege.controlserver)) return true;

        var roles = warp.AllowedRoles ?? new List<string>();
        var privileges = warp.RequiredPrivileges ?? new List<string>();
        if (roles.Count == 0 && privileges.Count == 0) return true;

        string roleCode = player.ServerData?.RoleCode ?? player.Role?.Code ?? "";
        bool roleMatch = roles.Any(r => r.Equals(roleCode, StringComparison.OrdinalIgnoreCase));
        bool anyPrivilege = privileges.Any(p => player.HasPrivilege(p));
        bool allPrivileges = privileges.Count == 0 || privileges.All(p => player.HasPrivilege(p));
        bool allMode = string.Equals(warp.AccessMode, "all", StringComparison.OrdinalIgnoreCase);

        bool allowed = allMode
            ? (roles.Count == 0 || roleMatch) && allPrivileges
            : roleMatch || anyPrivilege;

        if (!allowed) reason = AccessSummary(warp);
        return allowed;
    }

    private static string AccessSummary(WarpDefinition warp)
    {
        var roles = warp.AllowedRoles ?? new List<string>();
        var privileges = warp.RequiredPrivileges ?? new List<string>();
        if (roles.Count == 0 && privileges.Count == 0) return "Public";
        string roleText = roles.Count == 0 ? "" : "role: " + string.Join(", ", roles);
        string privilegeText = privileges.Count == 0 ? "" : "privilege: " + string.Join(", ", privileges);
        string joined = string.Join(string.Equals(warp.AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? " AND " : " OR ", new[] { roleText, privilegeText }.Where(x => x.Length > 0));
        return (string.Equals(warp.AccessMode, "all", StringComparison.OrdinalIgnoreCase) ? "All requirements — " : "Any match — ") + joined;
    }

    private List<string> GetAvailableRoles() => (api.Server.Config.Roles ?? new List<IPlayerRole>())
        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Code))
        .Select(r => r.Code)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<string> GetAvailablePrivileges()
    {
        var roles = api.Server.Config.Roles ?? new List<IPlayerRole>();
        return Privilege.AllCodes()
            .Concat(roles.Where(r => r != null).SelectMany(r => (r.Privileges ?? new List<string>()).Concat(r.RuntimePrivileges ?? new HashSet<string>())))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private WarpDefinition? Find(string name) => database.Warps.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private IServerPlayer? OnlineByUid(string uid) => api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerUID.Equals(uid, StringComparison.OrdinalIgnoreCase));
    private static string CooldownKey(string uid, string warp) => uid + "\n" + warp;
    private static int EffectiveCooldown(WarpDefinition warp, PlayerCommandConfig cfg) => warp.CooldownSeconds >= 0 ? warp.CooldownSeconds : cfg.WarpDefaultCooldownSeconds;
    private static int EffectiveWarmup(WarpDefinition warp, PlayerCommandConfig cfg) => warp.WarmupSeconds >= 0 ? warp.WarmupSeconds : cfg.WarpDefaultWarmupSeconds;
    private static string Display(WarpDefinition warp) => string.IsNullOrWhiteSpace(warp.DisplayName) ? warp.Name : warp.DisplayName;

    private void SaveDatabase()
    {
        database.Clamp();
        BlockLogger.Services.SuiteConfigStorage.Store(api, database, WarpDatabase.ConfigFile);
    }

    private WarpMutationResult Result(bool success, string message) => new() { Success = success, Message = message, Snapshot = GetSnapshot() };

    private static WarpDefinition Clone(WarpDefinition x) => new()
    {
        Name = x.Name,
        DisplayName = x.DisplayName,
        Enabled = x.Enabled,
        X = x.X,
        Y = x.Y,
        Z = x.Z,
        CooldownSeconds = x.CooldownSeconds,
        WarmupSeconds = x.WarmupSeconds,
        CancelOnMove = x.CancelOnMove,
        CostEnabled = x.CostEnabled,
        CostItemCode = x.CostItemCode,
        CostQuantity = x.CostQuantity,
        AccessMode = x.AccessMode,
        AllowedRoles = x.AllowedRoles?.ToList() ?? new List<string>(),
        RequiredPrivileges = x.RequiredPrivileges?.ToList() ?? new List<string>()
    };

    public static string NormalizeWarpName(string? value)
    {
        string raw = (value ?? "").Trim().ToLowerInvariant();
        if (raw.Length > 32) raw = raw[..32];
        return new string(raw.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
    }

    private void OnPlayerDisconnect(IServerPlayer player) => pendingByUid.Remove(player.PlayerUID);

    private static void Reply(IServerPlayer player, int groupId, string message, bool success) =>
        player.SendMessage(groupId, message, success ? EnumChatType.CommandSuccess : EnumChatType.CommandError);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (tickListenerId != 0) api.Event.UnregisterGameTickListener(tickListenerId);
        api.Event.PlayerDisconnect -= OnPlayerDisconnect;
        pendingByUid.Clear();
        lastWarpUtc.Clear();
    }
}
